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

#ifndef CMDMOVESTACKENDMSG_H
#define CMDMOVESTACKENDMSG_H

#include "commandmsg.h"
#include "d2assert.h"

namespace game {
struct TypeDescriptor;
struct CompleteObjectLocator;
} // namespace game

namespace hooks {

/** This custom message fixes a network desync issue described below.
 * The issue is that client may send multiple CStackMoveMsg while server still processes the first
 * one. This will typically lead to client game crash.
 * One of the known occurrences is a crash on entering a battle:
 * 1. A client sends CStackMoveMsg to a server while targeting an enemy stack
 * 2. The client increments CMidObjectLock::pendingNetworkUpdates
 * --- While CMidObjectLock is locked, client is not allowed to send further commands ---
 * 3. The server receives CStackMoveMsg and begins to process a list of possible effects
 * (IEventEffect)
 * --- Lets assume the most simple case where the first event to process is CEffectMove ---
 * 4. The server processes CEffectMove and sends CmdMoveStackMsg back to the client (followed by a
 * number of CRefreshInfo messages) and continues to process the list of events
 * --- Here assume that the server got a little busy processing all the events ---
 * 5. The client receives CmdMoveStackMsg and resets CMidObjectLock::pendingNetworkUpdates
 * 6. The player's stack is visually moved toward an enemy stack, but the battle is not yet to begin
 * --- Now client is free to send further commands to the server! ---
 * --- Here comes the interesting part. ---
 * 6. The player relentlessly clicking on the enemy stack sends another CStackMoveMsg to the server
 * 7. The server finally processed all the events to find out that there should be a battle to
 * begin!
 * 8. The server sends CCmdBattleStartMsg to the client
 * 9. The client receives CCmdBattleStartMsg and starts a battle
 * 10. Now guess what: the server receives another CStackMoveMsg (but the client is already in
 * battle!)
 * 11. The server manages to ignore the fact of battle already going, and sends another
 * CCmdBattleStartMsg to the client
 * 12. Finally, the client receives CCmdBattleStartMsg while already being in battle and happily
 * crashes to desktop
 */
class CCmdMoveStackEndMsg : public game::CCommandMsg
{
public:
    CCmdMoveStackEndMsg();
    ~CCmdMoveStackEndMsg();

    static const char* getName();

protected:
    static game::TypeDescriptor* getTypeDescriptor();
    static game::CompleteObjectLocator* getCompleteObjectLocator();

    // CNetMsg
    static void __fastcall destructor(CCmdMoveStackEndMsg* thisptr, int /*%edx*/, char flags);
    static void __fastcall serialize(CCmdMoveStackEndMsg* thisptr,
                                     int /*%edx*/,
                                     game::CMqStream* stream);

    // CCommandMsg
    static game::CommandMsgId __fastcall getId(const CCmdMoveStackEndMsg* thisptr, int /*%edx*/);
    static game::CommandMsgParam __fastcall getParam(const CCmdMoveStackEndMsg* thisptr,
                                                     int /*%edx*/);
    static bool __fastcall canIgnore(const CCmdMoveStackEndMsg* thisptr,
                                     int /*%edx*/,
                                     const game::IMidgardObjectMap* objectMap,
                                     const game::IdList* playerIds,
                                     const game::CMidgardID* currentPlayerId);
};

assert_offset(CCmdMoveStackEndMsg, vftable, 0);

} // namespace hooks

#endif // EXCHANGERESOURCESMSG_H
