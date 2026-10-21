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
#include "midgardobjectmap.h"
#include "midplayer.h"
#include "midstack.h"
#include "midunit.h"
#include "mqpoint.h"
#include "probe.h"
#include "probeaiutil.h"
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <fmt/format.h>
#include <intrin.h>
#include <string>
#include <vector>
#include <Windows.h>

/*
 * Схема корпуса 7.2: данные ИИ для остатка расхождений порта (docs/next-session.md «Хвосты ИИ»,
 * docs/ai-tactic-candidates.md §25-§26). Адреса - Discipl2.exe Gog 3.01, capstone; сигнатуры и
 * смещения - в docs/corpus-schema.md, раздел «Схема 7.2».
 *
 * ai_place_cache, ai_fort_cache - кэш целей тактики CMidServerLogicData2 (0x45e4c7, пересборка
 *   перед каждой оценкой отряда): сбор крепостей 0x45eb14 (фильтр 0x45ec77, охрана 0x45ed86), сбор
 *   мест 0x45efa4, отбор трёх 0x45f793 (вход - multimap после радиуса 0x45f5f1), исключения +0x6c.
 *   Кошелёк тактики CAiTactic+0x515c - при входе в тактику 0x46586c и в каждой строке кэша мест.
 * ai_strength_units - сила группы 0x5dcec7 по юнитам 0x5dd06c.
 * regen - дневное лечение 0x5d4099 (процент от максимума), слагаемое укрепления 0x5d4181 и юнита
 *   0x5d4588.
 * upgrade_target - цель повышения 0x5d7fe7 (не лидер) и 0x5d89c6 (лидер).
 * path_expand - порядок раскрытия узлов A* (0x680786) в поиске 0x5cc6da отряда не человека.
 * id_alloc - выдача нового id объекта сценария CMidgardScenarioMap слот 9 (0x5ee8bd).
 */

namespace probe {

namespace {

using namespace game;
using namespace aiutil;

using DynamicCast = void*(__cdecl*)(const void* object, int vfDelta, const void* source,
                                    const void* target, int isReference);
const auto engineDynamicCast = reinterpret_cast<DynamicCast>(0x66bda6);

/**
 * __RTDynamicCast движка, но без исключения наружу: на объекте без RTTI он бросает
 * __non_rtti_object, а исключение из хука роняет живой прогон. Ошибка - nullptr.
 */
void* rtDynamicCast(const void* object, int vfDelta, const void* source, const void* target,
                    int isReference)
{
    try {
        return engineDynamicCast(object, vfDelta, source, target, isReference);
    } catch (...) {
        return nullptr;
    }
}

constexpr std::uintptr_t rttiMapElement = 0x794828; // IMapElement
constexpr std::uintptr_t rttiMidObject = 0x797e68;  // IMidObject (id по +4)
constexpr std::uintptr_t rttiUsUnit = 0x7aad80;     // IUsUnit
constexpr std::uintptr_t rttiUmModifier = 0x7ae0a0; // CUmModifier

/** Id по адресу как JSON; пустой и недействительный id - null. */
std::string idOrNull(const void* p)
{
    CMidgardID id{};
    if (!readAt(p, 0, id) || id == emptyId || id == invalidId)
        return {"null"};
    return fmt::format(R"("{}")", idToString(&id));
}

std::string boolJson(bool value)
{
    return value ? "true" : "false";
}

/** Id объекта карты по IMapElement* - как 0x45f793: dynamic_cast к IMidObject, id по +4. */
std::string elementId(const void* element)
{
    if (!element || !readable(element, 4))
        return {"null"};
    const auto* object = rtDynamicCast(element, 0, reinterpret_cast<const void*>(rttiMapElement),
                                       reinterpret_cast<const void*>(rttiMidObject), 0);
    return object ? idOrNull(static_cast<const char*>(object) + 4) : std::string{"null"};
}

const void* elementObject(const void* element)
{
    if (!element || !readable(element, 4))
        return nullptr;
    return rtDynamicCast(element, 0, reinterpret_cast<const void*>(rttiMapElement),
                         reinterpret_cast<const void*>(rttiMidObject), 0);
}

/**
 * Обход дерева движка (Dinkumware xtree, d2tree.h): +4 длина, +0xc голова, +0x14 nil; узел: +4
 * родитель, +8 левый, +0xc правый, значение с +0x10. По порядку ключей, не дальше cap и длины.
 */
template <typename F>
void walkTree(const void* tree, std::size_t cap, F f)
{
    int length = 0;
    const char* head = nullptr;
    const char* nil = nullptr;
    if (!readAt(tree, 4, length) || !readAt(tree, 0xc, head) || !head || !readAt(tree, 0x14, nil))
        return;

    const char* node = nullptr;
    if (!readAt(head, 8, node))
        return;

    const auto count = std::min<std::size_t>(cap, length > 0 ? static_cast<std::size_t>(length) : 0);
    for (std::size_t i = 0; i < count && node && node != head; ++i) {
        f(node);

        const char* right = nullptr;
        if (!readAt(node, 0xc, right))
            return;
        if (right == nil) {
            const char* parent = nullptr;
            for (int guard = 0;; ++guard) {
                const char* parentRight = nullptr;
                if (guard > 64 || !readAt(node, 4, parent) || !parent
                    || !readAt(parent, 0xc, parentRight))
                    return;
                if (node != parentRight)
                    break;
                node = parent;
            }
            const char* nodeRight = nullptr;
            if (!readAt(node, 0xc, nodeRight) || !readAt(node, 4, parent))
                return;
            if (nodeRight != parent)
                node = parent;
        } else {
            node = right;
            for (int guard = 0;; ++guard) {
                const char* left = nullptr;
                if (guard > 64 || !readAt(node, 8, left))
                    return;
                if (left == nil)
                    break;
                node = left;
            }
        }
    }
}

constexpr std::size_t treeCap = 256;

/** Запись multimap кэша: ключ double +0x10, IMapElement* +0x18, клетка входа +0x1c. */
struct CacheEntry
{
    std::string id;
    std::string cls;
    std::string d;
    std::string pos;
};

std::vector<CacheEntry> cacheEntries(const void* tree, bool withClass)
{
    std::vector<CacheEntry> out;
    walkTree(tree, treeCap, [&out, withClass](const char* node) {
        const void* element = nullptr;
        readAt(node, 0x18, element);
        CacheEntry entry;
        entry.id = elementId(element);
        if (withClass) {
            const auto* object = elementObject(element);
            entry.cls = object ? fmt::format(R"("{}")", rttiClassName(object)) : "null";
        }
        entry.d = doubleAt(node, 0x10);
        entry.pos = pointJson(node + 0x1c);
        out.push_back(std::move(entry));
    });
    return out;
}

/** Id записей multimap кэша (для отбора трёх). */
std::vector<std::string> treeElementIds(const void* tree)
{
    std::vector<std::string> out;
    walkTree(tree, treeCap, [&out](const char* node) {
        const void* element = nullptr;
        readAt(node, 0x18, element);
        out.push_back(elementId(element));
    });
    return out;
}

/** Множество id (значение - CMidgardID по +0x10). */
std::vector<std::string> treeIds(const void* tree)
{
    std::vector<std::string> out;
    walkTree(tree, treeCap, [&out](const char* node) { out.push_back(idOrNull(node + 0x10)); });
    return out;
}

/** Вектор кэша {begin +0, end +4} указателей IMapElement*. */
std::vector<std::string> vectorElementIds(const void* vector)
{
    std::vector<std::string> out;
    const char* begin = nullptr;
    const char* end = nullptr;
    if (!readAt(vector, 0, begin) || !readAt(vector, 4, end) || end < begin)
        return out;
    const auto count = std::min<std::ptrdiff_t>((end - begin) / 4, 16);
    for (std::ptrdiff_t i = 0; i < count; ++i) {
        const void* element = nullptr;
        readAt(begin, static_cast<std::size_t>(i) * 4, element);
        out.push_back(elementId(element));
    }
    return out;
}

bool contains(const std::vector<std::string>& ids, const std::string& id)
{
    return std::find(ids.begin(), ids.end(), id) != ids.end();
}

std::string jsonList(const std::vector<std::string>& items)
{
    std::string out{"["};
    for (std::size_t i = 0; i < items.size(); ++i) {
        if (i)
            out += ',';
        out += items[i];
    }
    return out + ']';
}

std::string bankAt(const void* base, std::size_t offset)
{
    Bank bank{};
    return readAt(base, offset, bank) ? bankObject(bank) : std::string{"null"};
}

/* ---------------------------------------------------------------------------------------------
 * ai_place_cache, ai_fort_cache - 0x46586c, 0x45e4c7, 0x45eb14, 0x45efa4, 0x45ec77, 0x45ed86,
 * 0x45f793
 */

constexpr std::size_t tacticMapOffset = 8;
constexpr std::size_t tacticPlayerOffset = 0xc;
constexpr std::size_t tacticPurseOffset = 0x515c;
constexpr std::size_t tacticCacheOffset = 0x51c4; // CMidServerLogicData2*

constexpr std::size_t cacheFortsOffset = 0x1c;
constexpr std::size_t cachePlacesOffset = 0x2c;
constexpr std::size_t cacheExcludedOffset = 0x6c;

constexpr std::size_t fortOwnerOffset = 0x84;
constexpr std::size_t fortVisitorOffset = 0x8c;

/** Последняя тактика, вошедшая в состояние (0x46586c): кошелёк для строки кэша мест. */
std::atomic<const void*> lastTactic{nullptr};

struct FortCheck
{
    std::string id;
    bool pass{};
    int guard{-1}; // -1 - охрана не звалась
    std::string owner;
    std::string visitor;
};

struct CacheScope
{
    const void* cache{};
    std::vector<CacheEntry> fortsRaw;
    std::vector<CacheEntry> placesRaw;
    bool fortsCollected{};
    bool placesCollected{};
    std::vector<std::string> fortsKept;
    std::vector<std::string> placesKept;
    bool fortsKeptKnown{};
    bool placesKeptKnown{};
    std::vector<FortCheck> checks;
    bool inForts{};
};

thread_local CacheScope* cacheScope{nullptr};

using TacticEnter = int(__fastcall*)(void* thisptr, int /* %edx */, int arg);
TacticEnter origTacticEnter{};

int __fastcall tacticEnterProbe(void* thisptr, int /* %edx */, int arg)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const int result = origTacticEnter(thisptr, 0, arg);

    lastTactic.store(thisptr);

    const IMidgardObjectMap* objectMap = nullptr;
    readAt(thisptr, tacticMapOffset, objectMap);
    const auto* playerId = reinterpret_cast<const CMidgardID*>(static_cast<const char*>(thisptr)
                                                               + tacticPlayerOffset);
    const auto* player = readablePlayer(objectMap, playerId);

    countCall("ai_place_cache");
    emit(seq,
         fmt::format(
             R"("ev":"ai_place_cache","at":"enter","player":{},"owner":{},"purse":{},"bank":{},"stack":null,"pos":null,"places":null,"picked":null,"excluded":null,"ret_addr":"{}")",
             aiTurnPlayer(), idOrNull(playerId), bankAt(thisptr, tacticPurseOffset),
             player ? bankObject(player->bank) : std::string{"null"}, hexAddress(returnAddress)));
    return result;
}

/** Кошелёк тактики, чей кэш - 'cache' ([CAiTactic+0x51c4]), или null. */
std::string purseOfCache(const void* cache)
{
    const auto* tactic = lastTactic.load();
    const void* tacticCache = nullptr;
    if (!tactic || !readAt(tactic, tacticCacheOffset, tacticCache) || tacticCache != cache)
        return {"null"};
    return bankAt(tactic, tacticPurseOffset);
}

std::string whyOf(const std::string& id,
                  const std::vector<std::string>& kept,
                  bool keptKnown,
                  const std::vector<std::string>& excluded,
                  const std::vector<std::string>& picked)
{
    if (contains(picked, id))
        return "picked";
    if (!keptKnown)
        return "unknown";
    if (!contains(kept, id))
        return "radius";
    if (contains(excluded, id))
        return "excluded";
    return "rank";
}

std::string placesJson(const CacheScope& scope,
                       const std::vector<std::string>& excluded,
                       const std::vector<std::string>& picked)
{
    std::string out{"["};
    for (std::size_t i = 0; i < scope.placesRaw.size(); ++i) {
        const auto& e = scope.placesRaw[i];
        out += fmt::format(R"({}{{"id":{},"cls":{},"d":{},"pos":{},"why":"{}"}})", i ? "," : "", e.id,
                           e.cls, e.d, e.pos,
                           whyOf(e.id, scope.placesKept, scope.placesKeptKnown, excluded, picked));
    }
    return out + ']';
}

std::string fortsJson(const CacheScope& scope,
                      const std::vector<std::string>& excluded,
                      const std::vector<std::string>& picked)
{
    std::string out{"["};
    bool first = true;
    for (const auto& check : scope.checks) {
        const CacheEntry* entry = nullptr;
        for (const auto& e : scope.fortsRaw)
            if (e.id == check.id)
                entry = &e;

        std::string why;
        if (!check.pass)
            why = check.guard == 1 ? "guard" : "filter";
        else
            why = whyOf(check.id, scope.fortsKept, scope.fortsKeptKnown, excluded, picked);

        out += fmt::format(
            R"({}{{"id":{},"filter":{},"guard":{},"owner":{},"visitor":{},"d":{},"pos":{},"why":"{}"}})",
            first ? "" : ",", check.id, boolJson(check.pass),
            check.guard < 0 ? std::string{"null"} : boolJson(check.guard == 1), check.owner,
            check.visitor, entry ? entry->d : "null", entry ? entry->pos : "null", why);
        first = false;
    }
    return out + ']';
}

using CacheBuild = int(__fastcall*)(void* thisptr, int /* %edx */, IMidgardObjectMap* objectMap,
                                    const CMidgardID* stackId, void* a3, void* a4);
CacheBuild origCacheBuild{};

int __fastcall cacheBuildProbe(void* thisptr, int /* %edx */, IMidgardObjectMap* objectMap,
                               const CMidgardID* stackId, void* a3, void* a4)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    CacheScope scope;
    scope.cache = thisptr;
    auto* previous = cacheScope;
    cacheScope = &scope;
    const int result = origCacheBuild(thisptr, 0, objectMap, stackId, a3, a4);
    cacheScope = previous;

    const auto* cache = static_cast<const char*>(thisptr);
    const auto excluded = treeIds(cache + cacheExcludedOffset);
    const auto places = vectorElementIds(cache + cachePlacesOffset);
    const auto forts = vectorElementIds(cache + cacheFortsOffset);
    const auto stack = idOrNull(stackId);
    const auto pos = pointJson(cache + 4);
    const auto player = aiTurnPlayer();

    countCall("ai_place_cache");
    emit(seq,
         fmt::format(
             R"("ev":"ai_place_cache","at":"cache","player":{},"owner":null,"purse":{},"bank":null,"stack":{},"pos":{},"places":{},"picked":{},"excluded":{},"ret_addr":"{}")",
             player, purseOfCache(thisptr), stack, pos,
             scope.placesCollected ? placesJson(scope, excluded, places) : std::string{"null"},
             jsonList(places), jsonList(excluded), hexAddress(returnAddress)));

    countCall("ai_fort_cache");
    emit(nextSeq(),
         fmt::format(
             R"("ev":"ai_fort_cache","player":{},"stack":{},"pos":{},"forts":{},"picked":{},"excluded":{},"place_seq":{})",
             player, stack, pos,
             scope.fortsCollected ? fortsJson(scope, excluded, forts) : std::string{"null"},
             jsonList(forts), jsonList(excluded), seq));
    return result;
}

using CollectForts = int(__stdcall*)(IMidgardObjectMap* objectMap, const void* stack,
                                     const CMqPoint* pos, void* tree);
CollectForts origCollectForts{};

int __stdcall collectFortsProbe(IMidgardObjectMap* objectMap, const void* stack, const CMqPoint* pos,
                                void* tree)
{
    auto* scope = cacheScope;
    if (scope)
        scope->inForts = true;
    const int result = origCollectForts(objectMap, stack, pos, tree);
    if (scope) {
        scope->inForts = false;
        scope->fortsRaw = cacheEntries(tree, false);
        scope->fortsCollected = true;
    }
    return result;
}

using CollectPlaces = int(__stdcall*)(IMidgardObjectMap* objectMap, const CMqPoint* pos, void* tree);
CollectPlaces origCollectPlaces{};

int __stdcall collectPlacesProbe(IMidgardObjectMap* objectMap, const CMqPoint* pos, void* tree)
{
    const int result = origCollectPlaces(objectMap, pos, tree);
    if (auto* scope = cacheScope) {
        scope->placesRaw = cacheEntries(tree, true);
        scope->placesCollected = true;
    }
    return result;
}

using FortFilter = bool(__stdcall*)(IMidgardObjectMap* objectMap, const void* stack, const void* fort);
FortFilter origFortFilter{};
FortFilter origFortGuard{};

bool __stdcall fortFilterProbe(IMidgardObjectMap* objectMap, const void* stack, const void* fort)
{
    auto* scope = cacheScope;
    const bool track = scope && scope->inForts;
    if (track) {
        FortCheck check;
        check.id = objectId(fort);
        check.owner = idOrNull(fort ? static_cast<const char*>(fort) + fortOwnerOffset : nullptr);
        check.visitor = idOrNull(fort ? static_cast<const char*>(fort) + fortVisitorOffset : nullptr);
        scope->checks.push_back(std::move(check));
    }
    const std::size_t index = track ? scope->checks.size() - 1 : 0;
    const bool result = origFortFilter(objectMap, stack, fort);
    if (track)
        scope->checks[index].pass = result;
    return result;
}

bool __stdcall fortGuardProbe(IMidgardObjectMap* objectMap, const void* stack, const void* fort)
{
    const bool result = origFortGuard(objectMap, stack, fort);
    auto* scope = cacheScope;
    if (scope && scope->inForts && !scope->checks.empty())
        scope->checks.back().guard = result ? 1 : 0;
    return result;
}

using PickThree = int(__stdcall*)(int count, void* source, void* excluded, void* set100, void* out);
PickThree origPickThree{};

int __stdcall pickThreeProbe(int count, void* source, void* excluded, void* set100, void* out)
{
    if (auto* scope = cacheScope) {
        const auto* cache = static_cast<const char*>(scope->cache);
        const auto* target = static_cast<const char*>(out);
        if (target == cache + cacheFortsOffset) {
            scope->fortsKept = treeElementIds(source);
            scope->fortsKeptKnown = true;
        } else if (target == cache + cachePlacesOffset) {
            scope->placesKept = treeElementIds(source);
            scope->placesKeptKnown = true;
        }
    }
    return origPickThree(count, source, excluded, set100, out);
}

/* ---------------------------------------------------------------------------------------------
 * ai_strength_units - 0x5dcec7, 0x5dd06c
 */

using UnitHpMax = int(__thiscall*)(const void* unit);
const auto unitHpMax = reinterpret_cast<UnitHpMax>(0x5e987c);

constexpr std::size_t maxModifiers = 16;

/** Цепочка модификаторов юнита (unitImpl -> CUmModifier::data->prev), id модификаторов. */
std::string modifiersJson(const CMidUnit* unit)
{
    const void* current = unit->unitImpl;

    std::vector<std::string> ids;
    for (std::size_t i = 0; current && i < maxModifiers; ++i) {
        if (!readable(current, 8))
            break;
        const auto* modifier = rtDynamicCast(current, 0, reinterpret_cast<const void*>(rttiUsUnit),
                                             reinterpret_cast<const void*>(rttiUmModifier), 0);
        if (!modifier)
            break;
        const char* data = nullptr;
        if (!readAt(modifier, 4, data) || !data)
            break;
        ids.push_back(idOrNull(data + 8));
        if (!readAt(data, 4, current))
            break;
    }
    return jsonList(ids);
}

std::string unitTypeOf(const CMidUnit* unit)
{
    const void* impl = unit->unitImpl;
    return impl ? idOrNull(static_cast<const char*>(impl) + 4) : std::string{"null"};
}

struct StrengthScope
{
    std::string units;
    int count{};
    std::string armor{"null"};
};

thread_local StrengthScope* strengthScope{nullptr};

using UnitStrength = double(__stdcall*)(const CMidUnit* unit, int a2, int a3, int armor, int enemies,
                                        int full);
UnitStrength origUnitStrength{};

double __stdcall unitStrengthProbe(const CMidUnit* unit, int a2, int a3, int armor, int enemies,
                                   int full)
{
    const double result = origUnitStrength(unit, a2, a3, armor, enemies, full);
    auto* scope = strengthScope;
    if (!scope || !readable(unit, sizeof(CMidUnit)))
        return result;

    if (scope->count == 0)
        scope->armor = std::to_string(armor);
    ++scope->count;
    scope->units += fmt::format(
        R"({}{{"id":{},"type":{},"hp":{},"max":{},"s":{},"mods":{}}})", scope->units.empty() ? "" : ",",
        idOrNull(&unit->id), unitTypeOf(unit), unit->currentHp, unitHpMax(unit),
        jsonDouble(true, result), modifiersJson(unit));
    return result;
}

using GroupStrength = double(__stdcall*)(IMidgardObjectMap* objectMap, const CMidgardID* stackId,
                                         int a3, void* group, int enemies, int a6, int noCity,
                                         int full);
GroupStrength origGroupStrength{};

double __stdcall groupStrengthProbe(IMidgardObjectMap* objectMap, const CMidgardID* stackId, int a3,
                                    void* group, int enemies, int a6, int noCity, int full)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    StrengthScope scope;
    auto* previous = strengthScope;
    strengthScope = &scope;
    const double result = origGroupStrength(objectMap, stackId, a3, group, enemies, a6, noCity, full);
    strengthScope = previous;

    countCall("ai_strength_units");
    emit(seq,
         fmt::format(
             R"("ev":"ai_strength_units","player":{},"stack":{},"a3":{},"enemies":{},"a6":{},"city":{},"full":{},"armor":{},"units":[{}],"total":{},"ret_addr":"{}")",
             aiTurnPlayer(), idOrNull(stackId), a3, enemies, a6, boolJson((noCity & 0xff) == 0),
             boolJson((full & 0xff) != 0), scope.armor, scope.units, jsonDouble(true, result),
             hexAddress(returnAddress)));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * regen - 0x5d4099, 0x5d4181, 0x5d4588
 */

struct RegenScope
{
    const char* term{"other"};
    std::string owner{"null"};
    std::string inside{"null"};
    std::string bonus{"null"};
};

thread_local RegenScope* regenScope{nullptr};

constexpr std::size_t stackGroupOffset = 0x30;
constexpr std::size_t fortGroupOffset = 0x3c;
constexpr std::size_t stackIdOffset = 0x18;
constexpr std::size_t stackInsideOffset = 0x94;

/** Возвраты из 0x5d4181 (слагаемое укрепления) и 0x5d4588 (слагаемое юнита). */
constexpr std::uintptr_t fortTermVisitor = 0x5d4156;  // группа гостя крепости (0x5d40d9)
constexpr std::uintptr_t fortTermGarrison = 0x5d4164; // гарнизон (0x5d40d9)
constexpr std::uintptr_t unitTermStack = 0x5d43f6;    // отряды игрока (0x5d41d8)
constexpr std::uintptr_t unitTermGarrison = 0x5d4519; // гарнизоны игрока (0x5d41d8)

/**
 * Хозяин группы по адресу возврата вызова петли: отряд (группа CMidStack+0x30, id +0x18, крепость
 * стоянки +0x94) или крепость (группа гарнизона CFortification+0x3c, id +4). Незнакомый возврат -
 * хозяин null.
 */
void regenOwner(RegenScope& scope, const void* group, std::uintptr_t returnAddress)
{
    const auto* base = static_cast<const char*>(group);
    if (!base)
        return;
    if (returnAddress == fortTermVisitor || returnAddress == unitTermStack) {
        const auto* stack = base - stackGroupOffset;
        scope.owner = idOrNull(stack + stackIdOffset);
        scope.inside = idOrNull(stack + stackInsideOffset);
    } else if (returnAddress == fortTermGarrison || returnAddress == unitTermGarrison) {
        scope.owner = idOrNull(base - fortGroupOffset + 4);
    }
}

using RegenLoop = int(__stdcall*)(IMidgardObjectMap* objectMap, void* a2, void* a3);
RegenLoop origFortTerm{};
RegenLoop origUnitTerm{};

int __stdcall fortTermProbe(IMidgardObjectMap* objectMap, void* group, void* percent)
{
    const auto returnAddress = callerOf(_ReturnAddress());
    RegenScope scope;
    scope.term = "fort";
    regenOwner(scope, group, returnAddress);
    auto* previous = regenScope;
    regenScope = &scope;
    const int result = origFortTerm(objectMap, group, percent);
    regenScope = previous;
    return result;
}

int __stdcall unitTermProbe(IMidgardObjectMap* objectMap, void* bonus, void* group)
{
    const auto returnAddress = callerOf(_ReturnAddress());
    RegenScope scope;
    scope.term = "unit";
    scope.bonus = intAt(bonus, 0);
    regenOwner(scope, group, returnAddress);
    auto* previous = regenScope;
    regenScope = &scope;
    const int result = origUnitTerm(objectMap, bonus, group);
    regenScope = previous;
    return result;
}

using HealPercent = int(__stdcall*)(IMidgardObjectMap* objectMap, CMidUnit* unit, int* percent);
HealPercent origHealPercent{};

constexpr std::uintptr_t healFromRuin = 0x5d4706; // 0x5d463e - лечение в руине

int __stdcall healPercentProbe(IMidgardObjectMap* objectMap, CMidUnit* unit, int* percent)
{
    const bool known = readable(unit, sizeof(CMidUnit));
    const int before = known ? unit->currentHp : -1;
    const int max = known ? unitHpMax(unit) : -1;
    if (!known || before == max) {
        countCall("regen");
        return origHealPercent(objectMap, unit, percent);
    }

    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const int result = origHealPercent(objectMap, unit, percent);

    const auto* scope = regenScope;
    const char* term = scope ? scope->term : (returnAddress == healFromRuin ? "ruin" : "other");
    countCall("regen");
    emit(seq,
         fmt::format(
             R"("ev":"regen","term":"{}","unit":{},"type":{},"owner":{},"inside":{},"pct":{},"bonus":{},"hp_before":{},"hp_after":{},"hp_max":{},"ret_addr":"{}")",
             term, idOrNull(&unit->id), unitTypeOf(unit), scope ? scope->owner : "null",
             scope ? scope->inside : "null", intAt(percent, 0), scope ? scope->bonus : "null", before,
             unit->currentHp, max, hexAddress(returnAddress)));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * upgrade_target - 0x5d7fe7, 0x5d89c6
 */

using CanGrow = bool(__stdcall*)(const void* unitImpl);
const auto canGrow = reinterpret_cast<CanGrow>(0x5d8f84);
using IdOf = const CMidgardID*(__stdcall*)(const CMidgardID* unitId, const IMidgardObjectMap* map);
const auto unitOwner = reinterpret_cast<IdOf>(0x5eaad1);
const auto unitContainer = reinterpret_cast<IdOf>(0x5ea791);
using RacePlayer = CMidgardID*(__stdcall*)(CMidgardID* out, const IMidgardObjectMap* map,
                                           const void* unitImpl);
const auto racePlayer = reinterpret_cast<RacePlayer>(0x5d8883);
using RacePlayerOk = bool(__stdcall*)(const IMidgardObjectMap* map, const CMidgardID* playerId);
const auto racePlayerOk = reinterpret_cast<RacePlayerOk>(0x5d88be);

/** Id типа цели повышения: TUsUnitImpl* (id по +4) или null. */
std::string targetType(const void* impl)
{
    return impl ? idOrNull(static_cast<const char*>(impl) + 4) : std::string{"null"};
}

void emitUpgrade(std::uint64_t seq,
                 bool leader,
                 const IMidgardObjectMap* objectMap,
                 const CMidgardID* unitId,
                 const void* target,
                 const std::string& flag,
                 std::uintptr_t returnAddress)
{
    const auto* unit = readableUnit(objectMap, unitId);
    std::string type{"null"}, xp{"null"}, dyn{"null"}, grow{"null"}, owner{"null"},
        container{"null"}, race{"null"}, raceOk{"null"};
    if (unit) {
        type = unitTypeOf(unit);
        xp = std::to_string(unit->currentXp);
        dyn = boolJson(unit->dynLevel);
        const void* impl = unit->unitImpl;
        // Тот же порядок и те же условия, что у 0x5d7fe7: запросы без побочных действий.
        if (impl && readable(impl, 8)) {
            const bool grows = canGrow(impl);
            grow = boolJson(grows);
            if (grows) {
                CMidgardID raceId{emptyId};
                racePlayer(&raceId, objectMap, impl);
                race = idOrNull(&raceId);
                raceOk = boolJson(racePlayerOk(objectMap, &raceId));
                owner = idOrNull(unitOwner(unitId, objectMap));
                container = idOrNull(unitContainer(unitId, objectMap));
            }
        }
    }

    countCall("upgrade_target");
    emit(seq,
         fmt::format(
             R"("ev":"upgrade_target","leader":{},"player":{},"unit":{},"type":{},"xp":{},"dyn":{},"grow":{},"container":{},"owner":{},"race_player":{},"race_ok":{},"target":{},"flag":{},"ret_addr":"{}")",
             boolJson(leader), aiTurnPlayer(), idOrNull(unitId), type, xp, dyn, grow, container, owner,
             race, raceOk, targetType(target), flag, hexAddress(returnAddress)));
}

using UpgradeTarget = void*(__stdcall*)(void* out, IMidgardObjectMap* objectMap,
                                        const CMidgardID* unitId);
UpgradeTarget origUpgradeTarget{};

void* __stdcall upgradeTargetProbe(void* out, IMidgardObjectMap* objectMap, const CMidgardID* unitId)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    void* result = origUpgradeTarget(out, objectMap, unitId);

    const void* target = nullptr;
    readAt(out, 0, target);
    emitUpgrade(seq, false, objectMap, unitId, target, byteAt(out, 4), returnAddress);
    return result;
}

using LeaderUpgradeTarget = const void*(__stdcall*)(IMidgardObjectMap* objectMap,
                                                    const CMidgardID* unitId, int a3);
LeaderUpgradeTarget origLeaderUpgradeTarget{};

const void* __stdcall leaderUpgradeTargetProbe(IMidgardObjectMap* objectMap, const CMidgardID* unitId,
                                               int a3)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const void* result = origLeaderUpgradeTarget(objectMap, unitId, a3);
    emitUpgrade(seq, true, objectMap, unitId, result, "null", returnAddress);
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * path_expand - 0x5cc6da, 0x680786
 */

constexpr std::size_t maxExpanded = 300;
constexpr std::size_t maxPathPoints = 200;

struct ExpandScope
{
    std::string nodes;
    std::size_t count{};
};

thread_local ExpandScope* expandScope{nullptr};

/** Узел A*: +0 f (float), +4 g (float), +8 x, +0xc y, +0x10 родитель. */
struct AStarNode
{
    float f;
    float g;
    int x;
    int y;
};

/** float как JSON: кратчайшая точная запись, не конечное - null. */
std::string floatJson(float value)
{
    return std::isfinite(value) ? fmt::format("{}", value) : std::string{"null"};
}

using AStarExpand = void(__fastcall*)(void* thisptr, int /* %edx */, void* cost, void* node);
AStarExpand origAStarExpand{};

void __fastcall aStarExpandProbe(void* thisptr, int /* %edx */, void* cost, void* node)
{
    if (auto* scope = expandScope) {
        ++scope->count;
        AStarNode value{};
        if (scope->count <= maxExpanded && readAt(node, 0, value))
            scope->nodes += fmt::format("{}[{},{},{},{}]", scope->nodes.empty() ? "" : ",", value.x,
                                        value.y, floatJson(value.g), floatJson(value.f));
    }
    origAStarExpand(thisptr, 0, cost, node);
}

/** Отряд, путь которого пишется: найден и хозяин не человек. */
bool aiStack(const IMidgardObjectMap* objectMap, const CMidgardID* stackId, std::string& owner)
{
    const auto* stack = readableStack(objectMap, stackId);
    if (!stack)
        return false;
    const auto* player = readablePlayer(objectMap, &stack->ownerId);
    if (!player || player->isHuman)
        return false;
    owner = idOrNull(&stack->ownerId);
    return true;
}

/** Точки пути List<Pair<CMqPoint,int>> (+0x30): [x, y, накопленная цена]. */
std::string pathPointsJson(const void* finder)
{
    std::string out;
    const auto* list = static_cast<const char*>(finder) + 0x30;
    walkList(list, maxPathPoints, [&out](const void* node) {
        int point[3]{};
        if (!readAt(node, 8, point))
            return false;
        out += fmt::format("{}[{},{},{}]", out.empty() ? "" : ",", point[0], point[1], point[2]);
        return true;
    });
    return '[' + out + ']';
}

using PathFinder = void*(__fastcall*)(void* thisptr, int /* %edx */, IMidgardObjectMap* objectMap,
                                      const CMidgardID* stackId, const CMqPoint* target,
                                      int forbidWater, int a5, int mode, int a7, int a8, int a9,
                                      int a10);
PathFinder origPathFinder{};

void* __fastcall pathFinderProbe(void* thisptr, int /* %edx */, IMidgardObjectMap* objectMap,
                                 const CMidgardID* stackId, const CMqPoint* target, int forbidWater,
                                 int a5, int mode, int a7, int a8, int a9, int a10)
{
    std::string owner;
    if (!aiStack(objectMap, stackId, owner)) {
        countCall("path_expand");
        return origPathFinder(thisptr, 0, objectMap, stackId, target, forbidWater, a5, mode, a7, a8,
                              a9, a10);
    }

    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto targetText = pointJson(target);
    const auto stack = idOrNull(stackId);
    ExpandScope scope;
    auto* previous = expandScope;
    expandScope = &scope;
    void* result = origPathFinder(thisptr, 0, objectMap, stackId, target, forbidWater, a5, mode, a7,
                                  a8, a9, a10);
    expandScope = previous;

    const auto* finder = static_cast<const char*>(thisptr);
    countCall("path_expand");
    emit(seq,
         fmt::format(
             R"("ev":"path_expand","player":{},"stack":{},"owner":{},"target":{},"resolved":{},"forbid_water":{},"a5":{},"mode":{},"a8":{},"a10":{},"n":{},"cut":{},"nodes":[{}],"path":{},"end":{},"reach":{},"ret_addr":"{}")",
             aiTurnPlayer(), stack, owner, targetText, pointJson(finder + 0x10), forbidWater, a5, mode,
             a8, a10, scope.count, boolJson(scope.count > maxExpanded), scope.nodes,
             pathPointsJson(thisptr), pointJson(finder + 0x20), pointJson(finder + 0x28),
             hexAddress(returnAddress)));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * id_alloc - 0x5ee8bd
 */

constexpr int maxIdType = 0x40;
constexpr std::size_t freeIndexOffset = 0x30;
constexpr DWORD idStackFrames = 8;

using CreateId = CMidgardID*(__fastcall*)(void* thisptr, int /* %edx */, CMidgardID* out, int type);
CreateId origCreateId{};

CMidgardID* __fastcall createIdProbe(void* thisptr, int /* %edx */, CMidgardID* out, int type)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    std::string index{"null"};
    if (type >= 0 && type < maxIdType)
        index = intAt(thisptr, freeIndexOffset + static_cast<std::size_t>(type) * 4);

    void* frames[idStackFrames]{};
    const auto captured = CaptureStackBackTrace(0, idStackFrames, frames, nullptr);

    CMidgardID* result = origCreateId(thisptr, 0, out, type);

    std::vector<std::string> callers;
    for (USHORT i = 0; i < captured; ++i) {
        // Только адреса кода exe: кадры самого зонда и системных DLL не нужны.
        const auto address = reinterpret_cast<std::uintptr_t>(frames[i]);
        if (address >= codeBegin && address < codeEnd)
            callers.push_back(fmt::format(R"("{}")", hexAddress(address)));
    }

    countCall("id_alloc");
    emit(seq, fmt::format(
                  R"("ev":"id_alloc","player":{},"type":{},"id":{},"index":{},"ret_addr":"{}","callers":{})",
                  aiTurnPlayer(), type, idOrNull(out), index, hexAddress(returnAddress),
                  jsonList(callers)));
    return result;
}

} // namespace

} // namespace probe

namespace hooks {

void addAi72Hooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    const GogHook entries[] = {
        {"ai_tactic_enter",     0x46586c, (void*)tacticEnterProbe,       (void**)&origTacticEnter},
        {"ai_cache",            0x45e4c7, (void*)cacheBuildProbe,        (void**)&origCacheBuild},
        {"ai_cache_forts",      0x45eb14, (void*)collectFortsProbe,      (void**)&origCollectForts},
        {"ai_cache_places",     0x45efa4, (void*)collectPlacesProbe,     (void**)&origCollectPlaces},
        {"ai_fort_filter",      0x45ec77, (void*)fortFilterProbe,        (void**)&origFortFilter},
        {"ai_fort_guard",       0x45ed86, (void*)fortGuardProbe,         (void**)&origFortGuard},
        {"ai_cache_pick",       0x45f793, (void*)pickThreeProbe,         (void**)&origPickThree},
        {"ai_strength_group",   0x5dcec7, (void*)groupStrengthProbe,     (void**)&origGroupStrength},
        {"ai_strength_unit",    0x5dd06c, (void*)unitStrengthProbe,      (void**)&origUnitStrength},
        {"regen",               0x5d4099, (void*)healPercentProbe,       (void**)&origHealPercent},
        {"regen_fort_term",     0x5d4181, (void*)fortTermProbe,          (void**)&origFortTerm},
        {"regen_unit_term",     0x5d4588, (void*)unitTermProbe,          (void**)&origUnitTerm},
        {"upgrade_target",      0x5d7fe7, (void*)upgradeTargetProbe,     (void**)&origUpgradeTarget},
        {"upgrade_leader",      0x5d89c6, (void*)leaderUpgradeTargetProbe, (void**)&origLeaderUpgradeTarget},
        {"path_expand",         0x5cc6da, (void*)pathFinderProbe,        (void**)&origPathFinder},
        {"path_expand_node",    0x680786, (void*)aStarExpandProbe,       (void**)&origAStarExpand},
        {"id_alloc",            0x5ee8bd, (void*)createIdProbe,          (void**)&origCreateId},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
}

} // namespace hooks
