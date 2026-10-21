/*
 * This file is part of the modding toolset for Disciples 2.
 * (https://github.com/VladimirMakeev/D2ModdingToolset)
 * Copyright (C) 2024 Stanislav Egorov.
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

#include "phasegamehooks.h"
#include "midclient.h"
#include "midgard.h"
#include "midobjectlock.h"
#include "phasegame.h"
#include "stackmovemsg.h"
#include <spdlog/spdlog.h>

namespace hooks {

bool __fastcall phaseGameCheckObjectLockHooked(game::CPhaseGame* thisptr, int /*%edx*/)
{
    const auto* lock = thisptr->data->midObjectLock;
    if (lock->patched.exportingLeader) {
        spdlog::debug(__FUNCTION__ ": unlocked due to exportingLeader");
        return false;
    }

    if (lock->patched.movingStack) {
        return true;
    }

    return lock->pendingLocalUpdates || lock->pendingNetworkUpdates;
}

void __fastcall phaseGameSendStackMoveMsgHooked(
    game::CPhaseGame* thisptr,
    int /*%edx*/,
    const game::CMidgardID* stackId,
    const game::List<game::Pair<game::CMqPoint, int>>* movementPath,
    const game::CMqPoint* startPosition,
    const game::CMqPoint* endPosition)
{
    using namespace game;

    const auto& stackMoveMsgApi = CStackMoveMsgApi::get();

    auto* data = thisptr->data;
    if (!data->clientTakesTurn) {
        return;
    }

    ++data->midObjectLock->pendingNetworkUpdates;
    data->midObjectLock->patched.movingStack = true;
    spdlog::debug(
        __FUNCTION__ ": CMidObjectLock::movingStack set to true, pendingNetworkUpdates incremented to {:d}",
        data->midObjectLock->pendingNetworkUpdates);

    CStackMoveMsg message;
    stackMoveMsgApi.constructor2(&message, stackId, movementPath, startPosition, endPosition);

    CMidClient* client = data->midClient;
    CMidgard* midgard = client->core.data->midgard;
    CMidgardApi::get().sendNetMsgToServer(midgard, &message);

    stackMoveMsgApi.destructor(&message);
}

} // namespace hooks
