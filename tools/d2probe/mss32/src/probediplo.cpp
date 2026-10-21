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
#include "gameutils.h"
#include "middiplomacy.h"
#include "midgardid.h"
#include "midgardobjectmap.h"
#include "midplayer.h"
#include "racetype.h"
#include "version.h"
#include <cstdint>
#include <fmt/format.h>
#include <intrin.h>
#include <string>
#include <vector>

/*
 * Схема корпуса 5.2: мутатор дипломатических отношений.
 *
 * changeRelations, Gog 0x5d9366 (game-pristine/Discipl2.exe, ImageBase 0x400000), прочитан
 * дизассемблированием целиком:
 *
 *     bool __stdcall changeRelations(IMidgardObjectMap* map, const CMidgardID* playerA,
 *                                    const CMidgardID* playerB, int value, bool absolute)
 *
 * Пролог __EH_prolog (mov eax,0x6bb710; call 0x66bd10), аргументы [ebp+8]...[ebp+0x18], выход
 * ret 0x14 (0x5d959b) - пять четырёхбайтовых слотов. Оба id - игроки: мутатор сам ищет их через
 * findScenarioObjectById и приводит dynamic_cast к CMidPlayer. absolute движок сравнивает одним
 * байтом (0x5d94de), а обёртка атаки (0x5d9816) кладёт в слот dword, у которого задан только
 * младший байт, - поэтому слот берётся как int, передаётся дальше как есть и пишется по байту.
 *
 * Что делает: A == B - сразу true. Иначе цикл по игрокам карты X != B, кроме X с вечной войной
 * к B (бит 30, 0x5f7977):
 *     X == A:                          setRelation(A, B, absolute ? value : rel(A,B) + value)
 *     X != A и rel(X,B) <= D_WAR:      setRelation(A, X, rel(A,X) - value/2)   (0x5f7d94)
 * setRelation 0x5d959e (единственный вызов - 0x5d9551) обрезает значение в [0, 100] и сразу
 * применяет визитор CVisitorDiploCurrent (vftable 0x6f0c5c) режимом 1 через 0x5e68af, то есть
 * запись синхронна. Правило третьей стороны живёт ВНУТРИ этого же вызова, отдельного вызова
 * мутатора для него нет, - поэтому хук снимает весь список пар CMidDiplomacy до и после и пишет
 * каждую изменившуюся. Разбор - docs/corpus-schema.md, раздел «Дипломатические отношения».
 */

namespace probe {

namespace {

using namespace game;

constexpr std::uintptr_t gogChangeRelations = 0x5d9366;

/** Absent or unreadable values are journaled as this, never as a plausible number. */
constexpr int notComputed = -1;

/** Шесть рас дают 15 пар; больший размер - признак того, что читается не тот вектор. */
constexpr std::size_t maxRelations = 64;

/*
 * Причина изменения по адресу возврата. Мутатор зовут ровно девять мест .text (сканирование
 * call/jmp rel32 и imm32 адреса, других ссылок нет), каждое в своей обёртке с одним чтением GVars;
 * эффект, из которого зовётся обёртка, опознан по RTTI vftable его метода.
 */
struct CallSite
{
    std::uintptr_t returnAddress;
    const char* cause;
};

constexpr CallSite gogCallSites[] = {
    {0x5d935e, "tribute"},         // 0x5d932e D_GOLD +        <- CEffectGiveGold 0x4384de
    {0x5d96e4, "alliance_make"},   // 0x5d95f4 D_MK_ALLY =     <- CEffectChangeAlliance 0x43574e
    {0x5d9828, "attack"},          // 0x5d96fa D_ATTAK_PC =, SC/FO + <- CEffectBattle 0x433912
    {0x5d98a9, "rod_destroyed"},   // 0x5d985b D_ROD +  <- 0x5d9fab <- CEffectBreakRod 0x433c65
    {0x5d98da, "alliance_break"},  // 0x5d98b3 D_BK_ALLY =     <- CEffectChangeAlliance
    {0x5d9961, "alliance_refuse"}, // 0x5d98e2 D_REF_ALLY =    <- CEffectChangeAlliance
    {0x5d99ba, "noble"},           // 0x5d996e D_NOBLE +       <- CEffectNobleAction 0x43a335
    {0x5d9a2a, "trade_item"},      // 0x5d99c6 D_GOLD + <- 0x5d90d7 <- CEffectAcceptTrade 0x433805
    {0x5d9abe, "trade_spell"},     // 0x5d9a5b D_GOLD + <- 0x5d9134 <- CEffectAcceptTrade
};

const char* causeOf(std::uintptr_t returnAddress)
{
    for (const auto& site : gogCallSites) {
        if (site.returnAddress == returnAddress)
            return site.cause;
    }

    return "unknown";
}

using ChangeRelations = bool(__stdcall*)(IMidgardObjectMap* objectMap,
                                         const CMidgardID* playerA,
                                         const CMidgardID* playerB,
                                         int value,
                                         int absoluteSlot);
ChangeRelations origChangeRelations{};

using Relations = std::vector<DiplomacyRelation>;

/** Копия всех пар отношений карты, или пусто, если объект дипломатии прочитать нельзя. */
Relations relationsOf(const IMidgardObjectMap* objectMap)
{
    const auto* diplomacy = objectMap ? hooks::getDiplomacy(objectMap) : nullptr;
    if (!readable(diplomacy, sizeof(CMidDiplomacy)))
        return {};

    const auto* first = diplomacy->relations.bgn;
    const auto* last = diplomacy->relations.end;
    if (!first || last <= first || std::size_t(last - first) > maxRelations)
        return {};

    if (!readable(first, std::size_t(last - first) * sizeof(DiplomacyRelation)))
        return {};

    return Relations(first, last);
}

/** Раса игрока - LRaceCategory::id, то же число, что Grace.RACE_TYPE; иначе notComputed. */
int raceOf(const IMidgardObjectMap* objectMap, const CMidgardID* playerId)
{
    if (!objectMap || !readable(playerId, sizeof(CMidgardID)))
        return notComputed;

    const auto* player = hooks::getPlayer(objectMap, playerId);
    if (!readable(player, sizeof(CMidPlayer)) || !readable(player->raceType, sizeof(TRaceType)))
        return notComputed;

    // Движок берёт ту же цепочку: 0x5e4727 = [player+0x2c] -> [+8], id категории на +8 (0x5f79f9).
    const auto* data = player->raceType->data;
    return readable(data, sizeof(LRaceCategory)) ? static_cast<int>(data->raceType.id)
                                                 : notComputed;
}

/** Пара рас в любом порядке - так её ищет и движок (0x5f79f9). */
const DiplomacyRelation* findPair(const Relations& relations, int race1, int race2)
{
    for (const auto& entry : relations) {
        const int first = entry.race1CategoryId;
        const int second = entry.race2CategoryId;
        if ((first == race1 && second == race2) || (first == race2 && second == race1))
            return &entry;
    }

    return nullptr;
}

/** Отношение пары: текущее (биты 1-7), прошлое (8-14), союз (0), вечная война (30), слово. */
std::string relationObject(const DiplomacyRelation* entry)
{
    if (!entry)
        return {"null"};

    const std::uint32_t raw = entry->relation;
    return fmt::format(R"({{"cur":{},"prev":{},"ally":{},"eternal_war":{},"raw":{}}})",
                       (raw >> 1) & 0x7f, (raw >> 8) & 0x7f, (raw & 1) != 0,
                       ((raw >> 30) & 1) != 0, raw);
}

/** Все пары, чьё слово изменилось за вызов: основная пара и третьи стороны вместе. */
std::string changedPairs(const Relations& before, const Relations& after)
{
    std::string result{"["};
    const char* separator = "";
    for (const auto& now : after) {
        const auto* was = findPair(before, now.race1CategoryId, now.race2CategoryId);
        if (was && was->relation == now.relation)
            continue;

        result += fmt::format(R"({}{{"race1":{},"race2":{},"before":{},"after":{}}})", separator,
                              int(now.race1CategoryId), int(now.race2CategoryId),
                              was ? std::int64_t{was->relation} : std::int64_t{notComputed},
                              now.relation);
        separator = ",";
    }

    return result + ']';
}

/**
 * Мутатор отношений. Раса обоих игроков и весь список пар снимаются до вызова оригинала, список
 * пар - ещё раз после: строка несёт и то, что просили (value, absolute), и то, что реально
 * записалось, включая третьи стороны, а адрес возврата называет причину.
 */
bool __stdcall changeRelationsProbe(IMidgardObjectMap* objectMap,
                                    const CMidgardID* playerA,
                                    const CMidgardID* playerB,
                                    int value,
                                    int absoluteSlot)
{
    const auto seq = nextSeq();
    const auto returnAddress = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    const auto* map = readable(objectMap, sizeof(IMidgardObjectMap)) ? objectMap : nullptr;
    const int raceA = raceOf(map, playerA);
    const int raceB = raceOf(map, playerB);
    const auto before = relationsOf(map);

    const bool result = origChangeRelations(objectMap, playerA, playerB, value, absoluteSlot);

    const auto after = relationsOf(map);

    countCall("relation");
    emit(seq,
         fmt::format(
             R"("ev":"relation","a":"{}","b":"{}","race_a":{},"race_b":{},"value":{},"absolute":{},"ret_addr":"0x{:x}","cause":"{}","before":{},"after":{},"changed":{},"ret":{})",
             safeId(playerA), safeId(playerB), raceA, raceB, value, (absoluteSlot & 0xff) != 0,
             returnAddress, causeOf(returnAddress), relationObject(findPair(before, raceA, raceB)),
             relationObject(findPair(after, raceA, raceB)), changedPairs(before, after), result));
    return result;
}

} // namespace

int playerRace(const IMidgardObjectMap* objectMap, const CMidgardID* playerId)
{
    return raceOf(readableMap(objectMap) ? objectMap : nullptr, playerId);
}

std::string playerPairRelation(const IMidgardObjectMap* objectMap,
                               const CMidgardID* playerA,
                               const CMidgardID* playerB)
{
    const auto* map = readableMap(objectMap) ? objectMap : nullptr;
    return relationObject(findPair(relationsOf(map), raceOf(map, playerA), raceOf(map, playerB)));
}

bool playerPairWord(const IMidgardObjectMap* objectMap,
                    const CMidgardID* playerA,
                    const CMidgardID* playerB,
                    std::uint32_t& word)
{
    const auto* map = readableMap(objectMap) ? objectMap : nullptr;
    const auto relations = relationsOf(map);
    const auto* entry = findPair(relations, raceOf(map, playerA), raceOf(map, playerB));
    if (!entry)
        return false;

    word = entry->relation;
    return true;
}

} // namespace probe

namespace hooks {

void addDiploHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    // Адрес подтверждён только на Gog; на прочих сборках хук не ставится, как нуль в таблице.
    if (gameVersion() != GameVersion::Gog || !probe::hookEnabled("relation"))
        return;

    hooks.push_back({reinterpret_cast<void*>(probe::gogChangeRelations),
                     (void*)probe::changeRelationsProbe, (void**)&probe::origChangeRelations});
    installed.push_back("relation");
}

} // namespace hooks
