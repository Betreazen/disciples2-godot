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

#ifndef CBATLOGIC_H
#define CBATLOGIC_H

namespace game {

struct BattleMsgData;
struct CMidgardID;
struct IMidgardObjectMap;
struct IMidMsgSender;
struct CResultSender;
struct UnitInfo;

struct CBatLogic
{
    const void* vftable;
    BattleMsgData* battleMsgData;
    IMidgardObjectMap* objectMap;
};

namespace CBatLogicApi {

struct Api
{
    using BattleTurn = void*(__thiscall*)(CBatLogic* thisptr,
                                          CResultSender* resultSender,
                                          int groupBattleCondition,
                                          CMidgardID* a4,
                                          CMidgardID* a5);
    BattleTurn battleTurn;

    using UpdateGroupsIfBattleIsOver = void*(__thiscall*)(CBatLogic* thisptr,
                                                          CResultSender* resultSender);
    UpdateGroupsIfBattleIsOver updateGroupsIfBattleIsOver;

    using IsBattleOver = bool(__thiscall*)(CBatLogic* thisptr);
    IsBattleOver isBattleOver;

    using GetBattleWinnerGroupId = CMidgardID*(__thiscall*)(const CBatLogic* thisptr,
                                                      CMidgardID* winnerGroupId);
    GetBattleWinnerGroupId getBattleWinnerGroupId;

        using CheckAndDestroyEquippedBattleItems = void(__stdcall*)(IMidgardObjectMap* objectMap,
                                                                BattleMsgData* battleMsgData);
    CheckAndDestroyEquippedBattleItems checkAndDestroyEquippedBattleItems;


    using ApplyCBatAttackUnsummonEffect = void(__stdcall*)(IMidgardObjectMap* objectMap,
                                                           CMidgardID* unitId,
                                                           BattleMsgData* battleMsgData,
                                                           CResultSender* resultSender);
    ApplyCBatAttackUnsummonEffect applyCBatAttackUnsummonEffect;

    using ApplyCBatAttackUntransformEffect = void(__stdcall*)(IMidgardObjectMap* objectMap,
                                                              CMidgardID* unitId,
                                                              BattleMsgData* battleMsgData,
                                                              CResultSender* resultSender,
                                                              char sendResult);
    ApplyCBatAttackUntransformEffect applyCBatAttackUntransformEffect;

    using ApplyCBatAttackGroupUpgrade = void(__stdcall*)(IMidgardObjectMap* objectMap,
                                                         CMidgardID* winnerGroupId,
                                                         BattleMsgData* battleMsgData,
                                                         CResultSender* resultSender);
    ApplyCBatAttackGroupUpgrade applyCBatAttackGroupUpgrade;

    using ApplyCBatAttackGroupBattleCount = void(__stdcall*)(IMidgardObjectMap* objectMap,
                                                             CMidgardID* winnerGroupId,
                                                             BattleMsgData* battleMsgData,
                                                             CResultSender* resultSender);
    ApplyCBatAttackGroupBattleCount applyCBatAttackGroupBattleCount;

    using RestoreLeaderPositionsAfterDuel = void(__stdcall*)(IMidgardObjectMap* objectMap,
                                                             BattleMsgData* battleMsgData);
    RestoreLeaderPositionsAfterDuel restoreLeaderPositionsAfterDuel;

    using UpdateUnitsBattleXp = int(__stdcall*)(IMidgardObjectMap* objectMap,
                                                BattleMsgData* battleMsgData);
    UpdateUnitsBattleXp updateUnitsBattleXp;
};

Api& get();

const void* vftable();

} // namespace CBatLogicApi

} // namespace game

#endif // CBATLOGIC_H
