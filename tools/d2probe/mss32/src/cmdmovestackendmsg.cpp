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

#include "cmdmovestackendmsg.h"
#include "dynamiccast.h"
#include "mempool.h"
#include <spdlog/spdlog.h>

namespace hooks {

CCmdMoveStackEndMsg::CCmdMoveStackEndMsg()
{
    using namespace game;

    spdlog::debug(__FUNCTION__);

    CCommandMsgApi::get().constructor(this);

    static RttiInfo<CCommandMsgVftable> rttiInfo = {};
    if (rttiInfo.locator == nullptr) {
        rttiInfo.locator = getCompleteObjectLocator();
        rttiInfo.vftable.destructor = (CNetMsgVftable::Destructor)&destructor;
        rttiInfo.vftable.serialize = (CNetMsgVftable::Serialize)&serialize;
        rttiInfo.vftable.getId = (CCommandMsgVftable::GetId)&getId;
        rttiInfo.vftable.getParam = (CCommandMsgVftable::GetParam)&getParam;
        rttiInfo.vftable.canIgnore = (CCommandMsgVftable::CanIgnore)&canIgnore;
    }
    this->vftable = &rttiInfo.vftable;
}

CCmdMoveStackEndMsg::~CCmdMoveStackEndMsg()
{
    using namespace game;

    spdlog::debug(__FUNCTION__);

    CCommandMsgApi::get().destructor(this);
}

const char* CCmdMoveStackEndMsg::getName()
{
    return (*game::RttiApi::get().typeInfoRawName)(getTypeDescriptor());
}

game::TypeDescriptor* CCmdMoveStackEndMsg::getTypeDescriptor()
{
    using namespace game;

    // clang-format off
    static game::TypeDescriptor typeDescriptor{
        game::RttiApi::typeInfoVftable(),
        nullptr,
        ".?AVCCmdMoveStackEndMsg@@",
    };
    // clang-format on

    return &typeDescriptor;
}

game::CompleteObjectLocator* CCmdMoveStackEndMsg::getCompleteObjectLocator()
{
    using namespace game;

    // clang-format off
    static game::BaseClassDescriptor baseClassDescriptor{
        getTypeDescriptor(),
        2u, // base class array has 2 more elements after this descriptor
        game::PMD{ 0, -1, 0 },
        0u,
    };

    // We do not inherit from CCommandMsgTempl unlike other command messages
    static game::BaseClassArray baseClassArray{
        &baseClassDescriptor,
        RttiApi::rtti().CCommandMsgDescriptor,
        RttiApi::rtti().CNetMsgDescriptor,
    };

    static game::ClassHierarchyDescriptor classHierarchyDescriptor{
        0,
        0,
        3u, // base class array has a total of 3 elements
        &baseClassArray,
    };

    static game::CompleteObjectLocator completeObjectLocator{
        0u,
        offsetof(CCmdMoveStackEndMsg, vftable),
        0u,
        getTypeDescriptor(),
        &classHierarchyDescriptor,
    };
    // clang-format on

    return &completeObjectLocator;
}

void __fastcall CCmdMoveStackEndMsg::destructor(CCmdMoveStackEndMsg* thisptr,
                                                int /*%edx*/,
                                                char flags)
{
    thisptr->~CCmdMoveStackEndMsg();

    if (flags & 1) {
        spdlog::debug(__FUNCTION__ ": freeing memory");
        game::Memory::get().freeNonZero(thisptr);
    }
}

void __fastcall CCmdMoveStackEndMsg::serialize(CCmdMoveStackEndMsg* thisptr,
                                               int /*%edx*/,
                                               game::CMqStream* stream)
{
    game::CCommandMsgApi::get().serialize(thisptr, stream);
}

game::CommandMsgId __fastcall CCmdMoveStackEndMsg::getId(const CCmdMoveStackEndMsg* thisptr,
                                                         int /*%edx*/)
{
    return game::CommandMsgId::MoveStackEnd;
}

game::CommandMsgParam __fastcall CCmdMoveStackEndMsg::getParam(const CCmdMoveStackEndMsg* thisptr,
                                                               int /*%edx*/)
{
    return game::CommandMsgParam::Value0;
}

bool __fastcall CCmdMoveStackEndMsg::canIgnore(const CCmdMoveStackEndMsg* thisptr,
                                               int /*%edx*/,
                                               const game::IMidgardObjectMap* objectMap,
                                               const game::IdList* playerIds,
                                               const game::CMidgardID* currentPlayerId)
{
    // Looks like its always false for any command message.
    // If need arises, we can check if the stack belongs to currentPlayerId (return true if
    // not). Note that we should get playerId from the message, not look into objectMap to see who
    // the stack belongs (because the stack can change ownership during movement as event effect).
    return false;
}

} // namespace hooks
