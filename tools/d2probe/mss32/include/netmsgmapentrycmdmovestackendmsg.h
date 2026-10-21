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

#ifndef NETMSGMAPENTRYCMDMOVESTACKENDMSG_H
#define NETMSGMAPENTRYCMDMOVESTACKENDMSG_H

#include "netmsgmapentry.h"

namespace game {
struct TypeDescriptor;
struct CompleteObjectLocator;
} // namespace game

namespace hooks {

class CNetMsgMapEntryCmdMoveStackEndMsg : public game::CNetMsgMapEntry_member
{
public:
    CNetMsgMapEntryCmdMoveStackEndMsg(void* callbackThisptr, CNetMsgMapEntry_member::Callback callback);
    ~CNetMsgMapEntryCmdMoveStackEndMsg();

protected:
    static game::TypeDescriptor* getTypeDescriptor();
    static game::CompleteObjectLocator* getCompleteObjectLocator();

    // CNetMsgMapEntry
    static void __fastcall destructor(CNetMsgMapEntryCmdMoveStackEndMsg* thisptr,
                                      int /*%edx*/,
                                      char flags);
    static const char* __fastcall getName(const CNetMsgMapEntryCmdMoveStackEndMsg* thisptr,
                                          int /*%edx*/);
    static bool __fastcall process(CNetMsgMapEntryCmdMoveStackEndMsg* thisptr,
                                   int /*%edx*/,
                                   game::NetMessageHeader* header,
                                   std::uint32_t idFrom,
                                   std::uint32_t playerNetId);

    // CNetMsgMapEntry_member
    static bool __fastcall runCallback(CNetMsgMapEntryCmdMoveStackEndMsg* thisptr,
                                       int /*%edx*/,
                                       game::CNetMsg* netMessage,
                                       std::uint32_t idFrom,
                                       std::uint32_t playerNetId);
};

assert_offset(CNetMsgMapEntryCmdMoveStackEndMsg, vftable, 0);

} // namespace hooks

#endif // NETMSGMAPENTRYCMDMOVESTACKENDMSG_H
