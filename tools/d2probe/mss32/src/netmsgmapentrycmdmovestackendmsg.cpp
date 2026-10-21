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

#include "netmsgmapentrycmdmovestackendmsg.h"
#include "cmdmovestackendmsg.h"
#include "dynamiccast.h"
#include "mempool.h"
#include "streambits.h"
#include <spdlog/spdlog.h>

namespace hooks {

CNetMsgMapEntryCmdMoveStackEndMsg::CNetMsgMapEntryCmdMoveStackEndMsg(
    void* callbackThisptr,
    CNetMsgMapEntry_member::Callback callback)
{
    using namespace game;

    spdlog::debug(__FUNCTION__);

    static RttiInfo<CNetMsgMapEntry_memberVftable> rttiInfo = {};
    if (rttiInfo.locator == nullptr) {
        rttiInfo.locator = getCompleteObjectLocator();
        rttiInfo.vftable.destructor = (CNetMsgMapEntryVftable::Destructor)&destructor;
        rttiInfo.vftable.getName = (CNetMsgMapEntryVftable::GetName)&getName;
        rttiInfo.vftable.process = (CNetMsgMapEntryVftable::Process)&process;
        rttiInfo.vftable.runCallback = (CNetMsgMapEntry_memberVftable::RunCallback)&runCallback;
    }
    this->vftable = &rttiInfo.vftable;

    this->callbackThisptr = callbackThisptr;
    this->callback = callback;
}

CNetMsgMapEntryCmdMoveStackEndMsg::~CNetMsgMapEntryCmdMoveStackEndMsg()
{
    spdlog::debug(__FUNCTION__);
}

game::TypeDescriptor* CNetMsgMapEntryCmdMoveStackEndMsg::getTypeDescriptor()
{
    using namespace game;

    // clang-format off
    static game::TypeDescriptor typeDescriptor{
        game::RttiApi::typeInfoVftable(),
        nullptr,
        ".?AVCNetMsgMapEntryCmdMoveStackEndMsg@@",
    };
    // clang-format on

    return &typeDescriptor;
}

game::CompleteObjectLocator* CNetMsgMapEntryCmdMoveStackEndMsg::getCompleteObjectLocator()
{
    using namespace game;

    // clang-format off
    static game::BaseClassDescriptor baseClassDescriptor{
        getTypeDescriptor(),
        1u, // base class array has 1 more element after this descriptor
        game::PMD{ 0, -1, 0 },
        0u,
    };

    // We do not inherit from CNetMsgMapEntry_template unlike other CNetMsgMapEntry_member
    static game::BaseClassArray baseClassArray{
        &baseClassDescriptor,
        RttiApi::rtti().CNetMsgMapEntryDescriptor,
    };

    static game::ClassHierarchyDescriptor classHierarchyDescriptor{
        0,
        0,
        2u, // base class array has a total of 2 elements
        &baseClassArray,
    };

    static game::CompleteObjectLocator completeObjectLocator{
        0u,
        offsetof(CNetMsgMapEntryCmdMoveStackEndMsg, vftable),
        0u,
        getTypeDescriptor(),
        &classHierarchyDescriptor,
    };
    // clang-format on

    return &completeObjectLocator;
}

void __fastcall CNetMsgMapEntryCmdMoveStackEndMsg::destructor(
    CNetMsgMapEntryCmdMoveStackEndMsg* thisptr,
    int /*%edx*/,
    char flags)
{
    thisptr->~CNetMsgMapEntryCmdMoveStackEndMsg();

    if (flags & 1) {
        spdlog::debug(__FUNCTION__ ": freeing memory");
        game::Memory::get().freeNonZero(thisptr);
    }
}

const char* __fastcall CNetMsgMapEntryCmdMoveStackEndMsg::getName(
    const CNetMsgMapEntryCmdMoveStackEndMsg* thisptr,
    int /*%edx*/)
{
    return CCmdMoveStackEndMsg::getName();
}

bool __fastcall CNetMsgMapEntryCmdMoveStackEndMsg::process(
    CNetMsgMapEntryCmdMoveStackEndMsg* thisptr,
    int /*%edx*/,
    game::NetMessageHeader* header,
    std::uint32_t idFrom,
    std::uint32_t playerNetId)
{
    using namespace game;

    const auto& streamApi = CStreamBitsApi::get();

    CStreamBits stream;
    streamApi.readConstructor(&stream, 0, header, header->length, false);

    CCmdMoveStackEndMsg message;
    message.vftable->serialize((CNetMsg*)&message, &stream);

    bool result = thisptr->vftable->runCallback(thisptr, (CNetMsg*)&message, idFrom, playerNetId);

    streamApi.destructor(&stream);
    return result;
}

bool __fastcall CNetMsgMapEntryCmdMoveStackEndMsg::runCallback(
    CNetMsgMapEntryCmdMoveStackEndMsg* thisptr,
    int /*%edx*/,
    game::CNetMsg* netMessage,
    std::uint32_t idFrom,
    std::uint32_t playerNetId)
{
    return thisptr->callback(thisptr->callbackThisptr, netMessage, idFrom);
}

} // namespace hooks
