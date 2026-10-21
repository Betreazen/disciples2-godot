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

#include "batlogic.h"
#include "version.h"
#include <array>

namespace game::CBatLogicApi {

// clang-format off
static std::array<Api, 3> functions = {{
    // Akella
    Api{
        (Api::BattleTurn)0x625cda,
        (Api::UpdateGroupsIfBattleIsOver)0x62a9fc,
        (Api::IsBattleOver)0x62b3de,
        (Api::GetBattleWinnerGroupId)0x62b54f,
        (Api::CheckAndDestroyEquippedBattleItems)0x62ac15,
        (Api::ApplyCBatAttackUnsummonEffect)0x62a0c0,
        (Api::ApplyCBatAttackUntransformEffect)0x62725e,
        (Api::ApplyCBatAttackGroupUpgrade)0x62af01,
        (Api::ApplyCBatAttackGroupBattleCount)0x62b13e,
        (Api::RestoreLeaderPositionsAfterDuel)0x62b1d1,
        (Api::UpdateUnitsBattleXp)0x626dab,
    },
    // Russobit
    Api{
        (Api::BattleTurn)0x625cda,
        (Api::UpdateGroupsIfBattleIsOver)0x62a9fc,
        (Api::IsBattleOver)0x62b3de,
        (Api::GetBattleWinnerGroupId)0x62b54f,
        (Api::CheckAndDestroyEquippedBattleItems)0x62ac15,
        (Api::ApplyCBatAttackUnsummonEffect)0x62a0c0,
        (Api::ApplyCBatAttackUntransformEffect)0x62725e,
        (Api::ApplyCBatAttackGroupUpgrade)0x62af01,
        (Api::ApplyCBatAttackGroupBattleCount)0x62b13e,
        (Api::RestoreLeaderPositionsAfterDuel)0x62b1d1,
        (Api::UpdateUnitsBattleXp)0x626dab,
    },
    // Gog
    Api{
        (Api::BattleTurn)0x62481a,
        (Api::UpdateGroupsIfBattleIsOver)0x62953c,
        (Api::IsBattleOver)0x629f1e,
        (Api::GetBattleWinnerGroupId)0x62a08f,
        (Api::CheckAndDestroyEquippedBattleItems)0x629755,
        (Api::ApplyCBatAttackUnsummonEffect)0x628c00,
        (Api::ApplyCBatAttackUntransformEffect)0x625d9e,
        (Api::ApplyCBatAttackGroupUpgrade)0x629a41,
        (Api::ApplyCBatAttackGroupBattleCount)0x629c7e,
        (Api::RestoreLeaderPositionsAfterDuel)0x629d11,
        (Api::UpdateUnitsBattleXp)0x6258eb,
    }
}};

static std::array<const void*, 3> vftables = {{
    // Akella
    (const void*)0x6f3f4c,
    // Russobit
    (const void*)0x6f3f4c,
    // Gog
    (const void*)0x6f1efc,
}};
// clang-format on

Api& get()
{
    return functions[static_cast<int>(hooks::gameVersion())];
}

const void* vftable()
{
    return vftables[static_cast<int>(hooks::gameVersion())];
}

} // namespace game::CBatLogicApi
