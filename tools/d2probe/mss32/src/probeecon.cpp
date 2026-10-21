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
#include "fortcategory.h"
#include "fortification.h"
#include "game.h"
#include "gameutils.h"
#include "midgardid.h"
#include "midgardobjectmap.h"
#include "midplayer.h"
#include "midstack.h"
#include "midunit.h"
#include "midvillage.h"
#include "nobleactioncat.h"
#include "utils.h"
#include "version.h"
#include <atomic>
#include <cstdint>
#include <fmt/format.h>
#include <string>
#include <utility>
#include <vector>
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

/*
 * Corpus schema v5: the treasury, the five server handlers of map objects, equipping items,
 * healing, reviving, city growth, the building fact and the thief roll.
 *
 * Why the addresses live here and not in the game.cpp version table: that table is a positional
 * initialiser of four blocks, and a new field means thirteen new rows in each of them. A row off
 * by one there does not fail to compile - it silently binds a hook to the neighbouring function,
 * which is exactly the mistake this task was told to avoid. All of them are confirmed on the Gog
 * build only (game-pristine/Discipl2.exe, 4 474 880 bytes, ImageBase 0x400000), by
 * disassembling every entry point and reading its prologue, its argument slots and its 'ret'
 * width; the reasoning is written down in
 * disciples-modding/.superpowers/sdd/2026-09-09-f6plus-coverage/probe-v5.md. On any other build
 * every address below is zero and keep() drops the hook, exactly like a null in the version table.
 */

namespace probe {

namespace {

using namespace game;

/** Engine entry points, corpus schema v5. Zero on every build but Gog - see the file comment. */
struct EconAddresses
{
    /* CVisitorChangePlayerBank::apply, vftable 0x6f06d4 slot +8. __thiscall, no arguments. */
    std::uintptr_t changePlayerBankApply;
    /* CVisitorPlayerAddCapitalBuilding::apply, vftable 0x6f0714 slot +8. __thiscall, no args. */
    std::uintptr_t addCapitalBuildingApply;
    /* CMidServerLogic::onSite*, __stdcall; the widths are the 'ret' of each function. */
    std::uintptr_t onSiteBuyItem;  // ret 0x14 - 5 arguments
    std::uintptr_t onSiteSellItem; // ret 0x10 - 4 arguments
    std::uintptr_t onSiteBuySpell; // ret 0x14 - 5 arguments
    std::uintptr_t onSiteBuyUnit;  // ret 0x18 - 6 arguments
    std::uintptr_t onSiteTrainUnit; // ret 0x10 - 4 arguments
    /* Item visitors, ported from the upstream toolset (visitors.cpp Gog block). */
    std::uintptr_t equipItem;   // ret 0x14 - 5 arguments
    std::uintptr_t unEquipItem; // ret 0x10 - 4 arguments
    /* Fort services and the thief roll, __stdcall, three arguments each. */
    std::uintptr_t healUnit;
    std::uintptr_t reviveUnit;
    std::uintptr_t growCity;
    std::uintptr_t thiefRoll;
    /*
     * Порог броска вора, __stdcall с четырьмя аргументами (ret 0x10): первым идёт скрытый
     * указатель на возвращаемое по значению четырёхбайтовое поле, остальные три - те же, что у
     * thiefRoll. Читает Gaction.CHANCE и вычитает сопротивление цели, см. thiefChanceProbe.
     */
    std::uintptr_t thiefChance;
};

constexpr EconAddresses gogAddresses{
    0x60be8d, 0x60c496, 0x5d76e2, 0x5d77f5, 0x5d7952, 0x5d7aa1, 0x5d7c6b,
    0x5e741c, 0x5e746d, 0x5d724e, 0x5d733f, 0x5da01e, 0x443067, 0x5d31a1,
};

constexpr EconAddresses noAddresses{};

const EconAddresses& addresses()
{
    return hooks::gameVersion() == hooks::GameVersion::Gog ? gogAddresses : noAddresses;
}

/* Trampolines to the originals. Filled by Detours through HookInfo::original (main.cpp). */
using ThisCallNoArgs = bool(__fastcall*)(void* thisptr, int edx);
using StdCall3 = bool(__stdcall*)(const void*, const void*, const void*);
using StdCall4 = bool(__stdcall*)(const void*, const void*, const void*, const void*);
using StdCall5 = bool(__stdcall*)(const void*, const void*, const void*, const void*, const void*);
using StdCall6 = bool(__stdcall*)(const void*,
                                  const void*,
                                  const void*,
                                  const void*,
                                  const void*,
                                  const void*);

ThisCallNoArgs origChangePlayerBankApply{};
ThisCallNoArgs origAddCapitalBuildingApply{};
StdCall5 origOnSiteBuyItem{};
StdCall4 origOnSiteSellItem{};
StdCall5 origOnSiteBuySpell{};
StdCall6 origOnSiteBuyUnit{};
StdCall4 origOnSiteTrainUnit{};
StdCall5 origEquipItem{};
StdCall4 origUnEquipItem{};
StdCall3 origHealUnit{};
StdCall3 origReviveUnit{};
StdCall3 origGrowCity{};
StdCall3 origThiefRoll{};

/* Порог броска вора: возвращает указатель на записанное им же число (см. thiefChanceProbe). */
using ThiefChanceFn = int*(__stdcall*)(int*, const void*, const void*, const void*);
ThiefChanceFn origThiefChance{};

/** Absent or unreadable values are journaled as this, never as a plausible number. */
constexpr int notComputed = -1;

} // namespace

/**
 * True when 'size' bytes at 'p' can be read. Several of the arguments below are known to be ids
 * only by position, and a position read wrong dereferences an integer: the probe must never be
 * able to crash a live run of the game, so every id goes through here first.
 */
bool readable(const void* p, std::size_t size)
{
    if (!p)
        return false;

    MEMORY_BASIC_INFORMATION info{};
    if (VirtualQuery(p, &info, sizeof(info)) != sizeof(info) || info.State != MEM_COMMIT)
        return false;

    constexpr DWORD readableMask = PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY
                                   | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE
                                   | PAGE_EXECUTE_WRITECOPY;
    if (!(info.Protect & readableMask) || (info.Protect & PAGE_GUARD))
        return false;

    const auto end = reinterpret_cast<std::uintptr_t>(info.BaseAddress) + info.RegionSize;
    return reinterpret_cast<std::uintptr_t>(p) + size <= end;
}

/** Id at 'p' as a string, or "null" when nothing can be read there. */
std::string safeId(const void* p)
{
    if (!readable(p, sizeof(CMidgardID)))
        return {"null"};

    return idToString(static_cast<const CMidgardID*>(p));
}

bool readableMap(const IMidgardObjectMap* objectMap)
{
    const void* vftable = nullptr;
    return readAt(objectMap, 0, vftable) && readable(vftable, sizeof(IMidgardObjectMapVftable));
}

const CMidStack* readableStack(const IMidgardObjectMap* objectMap, const CMidgardID* stackId)
{
    if (!readableMap(objectMap) || !readable(stackId, sizeof(CMidgardID)))
        return nullptr;

    // getStack сам проверяет тип dynamic_cast: id не отряда даёт nullptr.
    const auto* stack = hooks::getStack(objectMap, stackId);
    return readable(stack, sizeof(CMidStack)) ? stack : nullptr;
}

const CMidUnit* readableUnit(const IMidgardObjectMap* objectMap, const CMidgardID* unitId)
{
    if (!readableMap(objectMap) || !readable(unitId, sizeof(CMidgardID)))
        return nullptr;

    const auto* unit = gameFunctions().findUnitById(objectMap, unitId);
    return readable(unit, sizeof(CMidUnit)) ? unit : nullptr;
}

const CMidPlayer* readablePlayer(const IMidgardObjectMap* objectMap, const CMidgardID* playerId)
{
    if (!readableMap(objectMap) || !readable(playerId, sizeof(CMidgardID)))
        return nullptr;

    const auto* player = hooks::getPlayer(objectMap, playerId);
    return readable(player, sizeof(CMidPlayer)) ? player : nullptr;
}

namespace {

/**
 * Field name for an id, taken from the id itself: the two letters at offset 4 of the string form
 * ("S143SI0000" -> "site") are the object type. Nothing is guessed from the shape of a signature -
 * a name that would be wrong simply does not appear, and the caller falls back to "idN".
 */
const char* idFieldName(const std::string& id)
{
    struct TypeName
    {
        char code[3];
        const char* name;
    };

    static constexpr TypeName names[] = {
        {"SI", "site"},     {"KC", "stack"},  {"UN", "unit"},    {"IM", "item"},
        {"IG", "item_type"}, {"SS", "spell"}, {"UU", "unit_type"}, {"PL", "player"},
        {"FT", "fort"},     {"BB", "building"}, {"NA", "action"}, {"RU", "ruin"},
        {"RD", "rod"},      {"BG", "bag"},    {"TB", "tomb"},    {"SR", "subrace"},
    };

    if (id.size() != 10)
        return nullptr;

    for (const auto& entry : names) {
        if (id[4] == entry.code[0] && id[5] == entry.code[1])
            return entry.name;
    }

    return nullptr;
}

} // namespace

/**
 * Builds the id part of an event line: one field per argument, named by the type of the id it
 * holds. A type that repeats, or one the table above does not know, is written as "idN" with N
 * the argument number - so two stacks in one call stay two distinct fields.
 */
std::string idsField(const void* const* args, std::size_t count)
{
    std::string result;
    std::vector<const char*> taken;

    for (std::size_t i = 0; i < count; ++i) {
        const auto id = safeId(args[i]);
        const char* name = idFieldName(id);
        for (const auto* used : taken) {
            if (name && used == name)
                name = nullptr;
        }

        if (name)
            taken.push_back(name);

        const auto field = name ? std::string{name} : fmt::format("id{}", i + 1);
        result += fmt::format(R"({}"{}":"{}")", i ? "," : "", field, id);
    }

    return result;
}

/** Bank as a JSON object, the same shape and order the 'income' event uses. */
std::string bankObject(const game::Bank& bank)
{
    return fmt::format(
        R"({{"gold":{},"infernal":{},"life":{},"death":{},"runic":{},"grove":{}}})", bank.gold,
        bank.infernalMana, bank.lifeMana, bank.deathMana, bank.runicMana, bank.groveMana);
}

namespace {

/*
 * CVisitorChangePlayerBank, read off the disassembly of apply (0x60be8d): the object map at +4,
 * the player id at +8, the twelve bytes of Bank at +0xc and the byte at +0x18 that picks between
 * the two arithmetic branches (0x5e4eac / 0x5e4e8a).
 */
struct ChangePlayerBankVisitor
{
    const void* vftable;
    IMidgardObjectMap* objectMap;
    CMidgardID playerId;
    Bank delta;
    std::uint8_t mode;
};

/*
 * CVisitorPlayerAddCapitalBuilding, read off apply (0x60c496): object map at +4, player at +8,
 * building at +0xc, a second id at +0x10 and the flag byte at +0x14.
 */
struct AddCapitalBuildingVisitor
{
    const void* vftable;
    IMidgardObjectMap* objectMap;
    CMidgardID playerId;
    CMidgardID buildingId;
    CMidgardID secondId;
    std::uint8_t flag;
};

/** The player's purse, or an empty Bank when the player cannot be read. */
Bank playerPurse(const IMidgardObjectMap* objectMap, const CMidgardID* playerId)
{
    const auto* player = objectMap ? hooks::getPlayer(objectMap, playerId) : nullptr;
    return player ? player->bank : Bank{};
}

/**
 * The treasury. Every purchase, hire, training, healing, revival and city growth in the engine
 * ends up here, which is why this is the one hook the whole of phase 7 hangs on: the purse is
 * read before and after the original call, so the line carries the actual movement and not just
 * the amount the visitor was built with.
 */
bool __fastcall changePlayerBankApplyProbe(ChangePlayerBankVisitor* thisptr, int /* %edx */)
{
    const auto seq = nextSeq();
    const bool readableThis = readable(thisptr, sizeof(ChangePlayerBankVisitor));
    const auto* map = readableThis ? thisptr->objectMap : nullptr;
    const Bank before = readableThis ? playerPurse(map, &thisptr->playerId) : Bank{};

    const bool result = origChangePlayerBankApply(thisptr, 0);

    const Bank after = readableThis ? playerPurse(map, &thisptr->playerId) : Bank{};

    countCall("bank");
    emit(seq, fmt::format(R"("ev":"bank","player":"{}","mode":{},"delta":{},"before":{},"after":{},"ret":{})",
                          readableThis ? idToString(&thisptr->playerId) : std::string{"null"},
                          readableThis ? int(thisptr->mode) : notComputed,
                          bankObject(readableThis ? thisptr->delta : Bank{}), bankObject(before),
                          bankObject(after), result));
    return result;
}

/** The fact of a building being added to the player. The price is not deducted here. */
bool __fastcall addCapitalBuildingApplyProbe(AddCapitalBuildingVisitor* thisptr, int /* %edx */)
{
    const auto seq = nextSeq();
    const bool readableThis = readable(thisptr, sizeof(AddCapitalBuildingVisitor));
    const auto player = readableThis ? idToString(&thisptr->playerId) : std::string{"null"};
    const auto building = readableThis ? idToString(&thisptr->buildingId) : std::string{"null"};
    const auto second = readableThis ? idToString(&thisptr->secondId) : std::string{"null"};

    const bool result = origAddCapitalBuildingApply(thisptr, 0);

    countCall("build");
    emit(seq, fmt::format(R"("ev":"build","player":"{}","building":"{}","id2":"{}","flag":{},"ret":{})",
                          player, building, second,
                          readableThis ? int(thisptr->flag) : notComputed, result));
    return result;
}

/** Writes one server-handler line: the ids the call carries, plus its answer. */
void emitSiteEvent(std::uint64_t seq,
                   const char* event,
                   const void* const* ids,
                   std::size_t count,
                   const std::string& extra,
                   bool result)
{
    countCall(event);
    emit(seq, fmt::format(R"("ev":"{}",{}{},"ret":{})", event, idsField(ids, count), extra,
                          result));
}

bool __stdcall onSiteBuyItemProbe(const void* objectMap,
                                  const void* siteId,
                                  const void* stackId,
                                  const void* itemId,
                                  const void* itemIndex)
{
    const auto seq = nextSeq();
    const void* ids[] = {siteId, stackId, itemId};

    const bool result = origOnSiteBuyItem(objectMap, siteId, stackId, itemId, itemIndex);

    // itemIndex is an int, not an id: -1 means "into the bag", anything else is the slot the item
    // is equipped into right away (equipItem, 0x5e741c, is called from here with it).
    const auto extra = fmt::format(
        R"(,"slot":{})", static_cast<int>(reinterpret_cast<std::intptr_t>(itemIndex)));
    emitSiteEvent(seq, "site_buy_item", ids, 3, extra, result);
    return result;
}

bool __stdcall onSiteSellItemProbe(const void* objectMap,
                                   const void* siteId,
                                   const void* id3,
                                   const void* id4)
{
    const auto seq = nextSeq();
    const void* ids[] = {siteId, id3, id4};

    const bool result = origOnSiteSellItem(objectMap, siteId, id3, id4);

    emitSiteEvent(seq, "site_sell_item", ids, 3, {}, result);
    return result;
}

bool __stdcall onSiteBuySpellProbe(const void* objectMap,
                                   const void* siteId,
                                   const void* id3,
                                   const void* id4,
                                   const void* id5)
{
    const auto seq = nextSeq();
    const void* ids[] = {siteId, id3, id4, id5};

    const bool result = origOnSiteBuySpell(objectMap, siteId, id3, id4, id5);

    emitSiteEvent(seq, "site_buy_spell", ids, 4, {}, result);
    return result;
}

bool __stdcall onSiteBuyUnitProbe(const void* objectMap,
                                  const void* siteId,
                                  const void* id3,
                                  const void* id4,
                                  const void* id5,
                                  const void* id6)
{
    const auto seq = nextSeq();
    const void* ids[] = {siteId, id3, id4, id5, id6};

    const bool result = origOnSiteBuyUnit(objectMap, siteId, id3, id4, id5, id6);

    emitSiteEvent(seq, "site_buy_unit", ids, 5, {}, result);
    return result;
}

bool __stdcall onSiteTrainUnitProbe(const void* objectMap,
                                    const void* siteId,
                                    const void* id3,
                                    const void* id4)
{
    const auto seq = nextSeq();
    const void* ids[] = {siteId, id3, id4};

    const bool result = origOnSiteTrainUnit(objectMap, siteId, id3, id4);

    emitSiteEvent(seq, "site_train", ids, 3, {}, result);
    return result;
}

/** equipItem(stackId, itemId, itemIndex, objectMap, apply) - apply 0 is only a question. */
bool __stdcall equipItemProbe(const void* stackId,
                              const void* itemId,
                              const void* itemIndex,
                              const void* objectMap,
                              const void* apply)
{
    const auto seq = nextSeq();
    const void* ids[] = {stackId, itemId};

    const bool result = origEquipItem(stackId, itemId, itemIndex, objectMap, apply);

    const auto extra = fmt::format(
        R"(,"slot":{},"apply":{})", static_cast<int>(reinterpret_cast<std::intptr_t>(itemIndex)),
        static_cast<int>(reinterpret_cast<std::intptr_t>(apply)));
    emitSiteEvent(seq, "item_equip", ids, 2, extra, result);
    return result;
}

/** unEquipItem(stackId, itemIndex, objectMap, apply). */
bool __stdcall unEquipItemProbe(const void* stackId,
                                const void* itemIndex,
                                const void* objectMap,
                                const void* apply)
{
    const auto seq = nextSeq();
    const void* ids[] = {stackId};

    const bool result = origUnEquipItem(stackId, itemIndex, objectMap, apply);

    const auto extra = fmt::format(
        R"(,"slot":{},"apply":{})", static_cast<int>(reinterpret_cast<std::intptr_t>(itemIndex)),
        static_cast<int>(reinterpret_cast<std::intptr_t>(apply)));
    emitSiteEvent(seq, "item_unequip", ids, 1, extra, result);
    return result;
}

bool __stdcall healUnitProbe(const void* objectMap, const void* id2, const void* id3)
{
    const auto seq = nextSeq();
    const void* ids[] = {id2, id3};

    const bool result = origHealUnit(objectMap, id2, id3);

    emitSiteEvent(seq, "heal", ids, 2, {}, result);
    return result;
}

/** HP и опыт юнита (CMidUnit +0x20/+0x24) или -1, если его не прочитать. */
std::pair<int, int> unitHpXp(const void* objectMap, const void* unitId)
{
    const auto* unit = readableUnit(static_cast<const IMidgardObjectMap*>(objectMap),
                                    static_cast<const CMidgardID*>(unitId));
    return unit ? std::pair{unit->currentHp, unit->currentXp} : std::pair{notComputed, notComputed};
}

/**
 * reviveUnit(map, unitId, playerId). Схема 6.0, партия 2: HP и опыт юнита до и после - визитор
 * воскрешения 0x612aec -> CMidUnit::revive 0x5e9a22 ставит hp = 1 и опыт не трогает.
 */
bool __stdcall reviveUnitProbe(const void* objectMap, const void* id2, const void* id3)
{
    const auto seq = nextSeq();
    const void* ids[] = {id2, id3};
    const auto before = unitHpXp(objectMap, id2);

    const bool result = origReviveUnit(objectMap, id2, id3);

    const auto after = unitHpXp(objectMap, id2);
    emitSiteEvent(seq, "revive", ids, 2,
                  fmt::format(R"(,"hp_before":{},"hp_after":{},"xp_before":{},"xp_after":{})",
                              before.first, after.first, before.second, after.second),
                  result);
    return result;
}

bool __stdcall growCityProbe(const void* objectMap, const void* id2, const void* id3)
{
    const auto seq = nextSeq();
    const void* ids[] = {id2, id3};

    const bool result = origGrowCity(objectMap, id2, id3);

    emitSiteEvent(seq, "city_grow", ids, 2, {}, result);
    return result;
}

/*
 * Порог броска вора, 0x5d31a1. Прочитан по машинному коду GOG-сборки целиком:
 *
 *     chance = globalData->actions[action]  (+0x10 записи, то есть Gaction.CHANCE)
 *     если chance < 100 и цель - отряд (тип id 0x23) либо укрепление (0x1d),
 *         chance -= сопротивление цели
 *     если chance < 0, chance = 0
 *     *out = chance;  return out
 *
 * Оригинал зовёт её первой строкой (0x443080), читает результат тривиальным геттером
 * 0x566368 (`mov eax,[ecx]; ret`) и сравнивает с ним бросок. Отсюда и способ достать порог:
 * хук на саму функцию и чтение того числа, которое она записала. Второй мыслимый способ -
 * позвать 0x5d31a1 ещё раз из обёртки thiefRoll - отвергнут: он повторно исполняет движковый
 * код с dynamic_cast и виртуальными вызовами по указателям, проверенным только по позиции, а
 * зонд не должен уметь уронить живой прогон. Наблюдение ничего не исполняет заново и даёт
 * ровно то число, по которому движок и принял решение.
 *
 * Вызывающих у 0x5d31a1 ровно двое (проверено сканированием .text на E8/E9): сам бросок
 * (0x443080) и интерфейс (0x49b446), который показывает шанс игроку. Поэтому значение
 * запоминается вместе со счётчиком вызовов, а thiefRollProbe берёт его только тогда, когда
 * счётчик сдвинулся ровно на единицу за время вызова оригинала.
 */
std::atomic<int> lastThiefChance{notComputed};
std::atomic<std::uint64_t> thiefChanceCalls{0};

int* __stdcall thiefChanceProbe(int* out,
                                const void* objectMap,
                                const void* action,
                                const void* targetId)
{
    int* const result = origThiefChance(out, objectMap, action, targetId);

    lastThiefChance.store(readable(result, sizeof(int)) ? *result : notComputed);
    thiefChanceCalls.fetch_add(1);
    countCall("thief_chance");
    return result;
}

/**
 * Категория действия вора (`LNobleActionCat::id`, nobleactioncat.h) как число, или notComputed,
 * если по указателю нет читаемой категории. Категория - это {vftable, таблица, id}, и движок
 * сам проверяет таблицу на ноль (0x5945e0), так что нулевая таблица здесь - признак того, что
 * аргумент не категория, а что-то другое.
 */
int nobleActionId(const void* action)
{
    if (!readable(action, sizeof(LNobleActionCat)))
        return notComputed;

    const auto* category = static_cast<const LNobleActionCat*>(action);
    return category->table ? static_cast<int>(category->id) : notComputed;
}

/**
 * The thief roll: success is (rand(0..99) + rand(0..99)) / 2 <= chance, i.e. a triangular draw and
 * not a flat one (0x4430ae..0x4430c1).
 *
 * Аргументы разобраны по машинному коду, а не по позиции: objectMap (виртуальный вызов +0x14 у
 * вызывающего 0x442fe1), затем указатель на категорию действия вора и указатель на id цели -
 * движок берёт у него тип (`CMidgardID::getType`, 0x57f3b6) и сравнивает с 0x23 (отряд) и 0x1d
 * (укрепление). Прежняя схема писала оба аргумента как id: у первого получалась постоянная
 * бессмыслица (`id1` = "G001CRedb4" во всех четырнадцати бросках первой большой партии - это
 * vftable категории, прочитанный как id), а второй попадал в поле `stack` и читался как «вор»,
 * хотя это цель (четыре броска из четырнадцати называли отряд нейтралов, который воров не водит).
 */
bool __stdcall thiefRollProbe(const void* objectMap, const void* action, const void* targetId)
{
    const auto seq = nextSeq();
    const auto rngBefore = rngCount();
    const auto chanceCallsBefore = thiefChanceCalls.load();

    const bool result = origThiefRoll(objectMap, action, targetId);

    const auto rngAfter = rngCount();
    const bool chanceIsOurs = thiefChanceCalls.load() == chanceCallsBefore + 1;

    countCall("thief_roll");
    emit(seq,
         fmt::format(
             R"("ev":"thief_roll","action":{},"target":"{}","dice1":{},"dice2":{},"chance":{},"rng_before":{},"rng_after":{},"ret":{})",
             nobleActionId(action), safeId(targetId), rngResultAt(rngBefore + 1),
             rngResultAt(rngBefore + 2), chanceIsOurs ? lastThiefChance.load() : notComputed,
             rngBefore, rngAfter, result));
    return result;
}

/** One fortification as a JSON object for the 'forts' array of 'fort_day'. */
std::string fortObject(const CFortification* fort, const IMidgardObjectMap* objectMap)
{
    const auto* vftable = reinterpret_cast<const CFortificationVftable*>(fort->vftable);
    const auto* category = vftable->getCategory(fort);
    const auto* villageCategory = FortCategories::get().village;
    const bool isVillage = category && villageCategory && category->id == villageCategory->id;
    const auto* village = isVillage ? static_cast<const CMidVillage*>(fort) : nullptr;

    // Схема 6.0: protect_b - CMidVillage +0xa0 (PROTECT_B записи .sg), второй модификатор защиты
    // деревни в геттере 0x5fe1f6; у столицы защита расы, и поле null. 'capacity' - слот +0x20,
    // имя тулсета getMaxUnitsInGarrison, но это GcityInf.SCOUT - радиус обзора (0x5fe1d8), имя
    // поля оставлено ради совместимости.
    return fmt::format(
        R"({{"id":"{}","owner":"{}","subrace":"{}","stack":"{}","tier":{},"capacity":{},"size":{},"growth_t":{},"riot_t":{},"regen":{},"protect_b":{}}})",
        idToString(&fort->id), idToString(&fort->ownerId), idToString(&fort->subraceId),
        idToString(&fort->stackId), vftable->getTier(fort, objectMap),
        vftable->getMaxUnitsInGarrison(fort, objectMap), village ? village->tierLevel : notComputed,
        village ? village->growthTurn : notComputed, village ? village->riotTurn : notComputed,
        village ? village->regeneration : notComputed,
        village ? fmt::format(R"("{}")", idToString(&village->protectionId)) : std::string{"null"});
}

} // namespace

void emitFortDay(const IMidgardObjectMap* objectMap, const CMidgardID* playerId, int turn)
{
    const auto seq = nextSeq();
    countCall("fort_day");

    std::string forts{"["};
    const char* separator = "";
    if (objectMap && playerId) {
        hooks::forEachScenarioObject(
            objectMap, IdType::Fortification, [&](const IMidScenarioObject* object) {
                const auto* fort = static_cast<const CFortification*>(object);
                // Only this player's forts: the hook runs once per player per day, so writing
                // every fort every time would repeat the whole map as many times as there are
                // players. A neutral village shows up here the day after it is captured.
                if (!fort || fort->ownerId.value != playerId->value)
                    return;

                forts += separator + fortObject(fort, objectMap);
                separator = ",";
            });
    }
    forts += ']';

    emit(seq, fmt::format(R"("ev":"fort_day","player":"{}","turn":{},"forts":{})",
                          idToString(playerId), turn, forts));
}

} // namespace probe

namespace hooks {

namespace {

/** Keeps one v5 hook when its address is known for this build and probe/hooks.txt allows it. */
void keep(const char* name,
          std::uintptr_t target,
          void* hook,
          void** original,
          Hooks& hooks,
          std::vector<std::string>& installed)
{
    if (!target || !probe::hookEnabled(name))
        return;

    hooks.push_back({reinterpret_cast<void*>(target), hook, original});
    installed.push_back(name);
}

} // namespace

void addEconHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;
    const auto& a = addresses();

    // clang-format off
    keep("bank", a.changePlayerBankApply, (void*)changePlayerBankApplyProbe, (void**)&origChangePlayerBankApply, hooks, installed);
    keep("build", a.addCapitalBuildingApply, (void*)addCapitalBuildingApplyProbe, (void**)&origAddCapitalBuildingApply, hooks, installed);
    keep("site_buy_item", a.onSiteBuyItem, (void*)onSiteBuyItemProbe, (void**)&origOnSiteBuyItem, hooks, installed);
    keep("site_sell_item", a.onSiteSellItem, (void*)onSiteSellItemProbe, (void**)&origOnSiteSellItem, hooks, installed);
    keep("site_buy_spell", a.onSiteBuySpell, (void*)onSiteBuySpellProbe, (void**)&origOnSiteBuySpell, hooks, installed);
    keep("site_buy_unit", a.onSiteBuyUnit, (void*)onSiteBuyUnitProbe, (void**)&origOnSiteBuyUnit, hooks, installed);
    keep("site_train", a.onSiteTrainUnit, (void*)onSiteTrainUnitProbe, (void**)&origOnSiteTrainUnit, hooks, installed);
    keep("item_equip", a.equipItem, (void*)equipItemProbe, (void**)&origEquipItem, hooks, installed);
    keep("item_unequip", a.unEquipItem, (void*)unEquipItemProbe, (void**)&origUnEquipItem, hooks, installed);
    keep("heal", a.healUnit, (void*)healUnitProbe, (void**)&origHealUnit, hooks, installed);
    keep("revive", a.reviveUnit, (void*)reviveUnitProbe, (void**)&origReviveUnit, hooks, installed);
    keep("city_grow", a.growCity, (void*)growCityProbe, (void**)&origGrowCity, hooks, installed);
    keep("thief_roll", a.thiefRoll, (void*)thiefRollProbe, (void**)&origThiefRoll, hooks, installed);
    keep("thief_chance", a.thiefChance, (void*)thiefChanceProbe, (void**)&origThiefChance, hooks, installed);
    // clang-format on
}

} // namespace hooks
