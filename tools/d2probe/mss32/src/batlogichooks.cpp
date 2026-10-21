/*
 * This file is part of the modding toolset for Disciples 2.
 * (https://github.com/VladimirMakeev/D2ModdingToolset)
 * Copyright (C) 2025 Alexey Voskresensky.
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

#include "batlogichooks.h"
#include "batlogic.h"
#include "battlemsgdata.h"
#include "game.h"
#include "midunit.h"
#include "originalfunctions.h"
#include "unitinfolist.h"
#include "unitinfolist.h"


namespace hooks {

void __fastcall battleTurnHooked(game::CBatLogic* thisptr,
                                 int /*%edx*/,
                                 game::CResultSender* resultSender,
                                 int groupBattleCondition,
                                 game::CMidgardID* a4,
                                 game::CMidgardID* a5)
{
    using namespace game;

    const auto& batLogicApi = CBatLogicApi::get();

    batLogicApi.updateUnitsBattleXp(thisptr->objectMap, thisptr->battleMsgData);

    getOriginalFunctions().battleTurn(thisptr, resultSender, groupBattleCondition, a4, a5);
}

void __fastcall updateGroupsIfBattleIsOverHooked(game::CBatLogic* thisptr,
                                                 int /*%edx*/,
                                                 game::CResultSender* resultSender)
{
    using namespace game;

    if (CBatLogicApi::get().isBattleOver(thisptr)) {
        const auto& batLogicApi = CBatLogicApi::get();
        const auto& battleApi = BattleMsgDataApi::get();
        const auto& fn = game::gameFunctions();
        const auto& listApi = UnitInfoListApi::get();
        const auto& idApi = CMidgardIDApi::get();

        batLogicApi.checkAndDestroyEquippedBattleItems(thisptr->objectMap, thisptr->battleMsgData);

        UnitInfoList unitInfos{};
        listApi.constructor(&unitInfos);
        battleApi.getUnitInfos(thisptr->battleMsgData, &unitInfos, false);
        listApi.sortBy(&unitInfos, listApi.compareByUnsummonOrder);

        for (const auto& unitInfo : unitInfos) {
            CMidgardID unitId;
            idApi.validateId(&unitId, unitInfo.unitId1);

            CMidUnit* unit = fn.findUnitById(thisptr->objectMap, &unitId);
            if (unit) {
                if (battleApi.getUnitStatus(thisptr->battleMsgData, &unitId, BattleStatus::Summon)) {
                    batLogicApi.applyCBatAttackUnsummonEffect(thisptr->objectMap, &unitId,
                                                              thisptr->battleMsgData, resultSender);

                } else if (unit->transformed) {
                    auto retreated = battleApi.getUnitStatus(thisptr->battleMsgData, &unitId,
                                                             BattleStatus::Retreated);

                    batLogicApi.applyCBatAttackUntransformEffect(thisptr->objectMap, &unitId,
                                                                 thisptr->battleMsgData,
                                                                 resultSender, !retreated);
                }
            }
        }

        
        CMidgardID winnerGroup1Id;
        batLogicApi.getBattleWinnerGroupId(thisptr, &winnerGroup1Id);

        BattleMsgData* battleMsgData1 = thisptr->battleMsgData;

        batLogicApi.applyCBatAttackGroupUpgrade(thisptr->objectMap, &winnerGroup1Id, battleMsgData1,
                                                resultSender);

        BattleMsgData* battleMsgData2 = thisptr->battleMsgData;

        CMidgardID winnerGroup2Id;
        batLogicApi.getBattleWinnerGroupId(thisptr, &winnerGroup2Id);


        batLogicApi.applyCBatAttackGroupBattleCount(thisptr->objectMap, &winnerGroup2Id,
                                                    battleMsgData2, resultSender);

        batLogicApi.restoreLeaderPositionsAfterDuel(thisptr->objectMap, thisptr->battleMsgData);

        listApi.destructor(&unitInfos);
    }
}

} // namespace hooks
