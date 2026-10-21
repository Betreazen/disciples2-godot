/*
 * This file is part of the modding toolset for Disciples 2.
 * (https://github.com/VladimirMakeev/D2ModdingToolset)
 * Copyright (C) 2022 Vladimir Makeev.
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

#ifndef STACKMOVEMSG_H
#define STACKMOVEMSG_H

#include "d2list.h"
#include "d2pair.h"
#include "midgardid.h"
#include "mqpoint.h"
#include "netmsg.h"

namespace game {

struct CStackMoveMsg : public CNetMsg
{
    CMidgardID stackId;
    CMqPoint endPosition;
    CMqPoint startPosition;
    List<Pair<CMqPoint, int>> movementPath;
};

assert_size(CStackMoveMsg, 40);

namespace CStackMoveMsgApi {

struct Api
{
    using Constructor = CStackMoveMsg*(__thiscall*)(CStackMoveMsg* thisptr);
    Constructor constructor;

    using Constructor2 = CStackMoveMsg*(__thiscall*)(CStackMoveMsg* thisptr,
                                                     const CMidgardID* stackId,
                                                     const List<Pair<CMqPoint, int>>* movementPath,
                                                     const CMqPoint* startPosition,
                                                     const CMqPoint* endPosition);
    Constructor2 constructor2;

    using Destructor = void(__thiscall*)(CStackMoveMsg* thisptr);
    Destructor destructor;
};

Api& get();

} // namespace CStackMoveMsgApi

} // namespace game

#endif // STACKMOVEMSG_H
