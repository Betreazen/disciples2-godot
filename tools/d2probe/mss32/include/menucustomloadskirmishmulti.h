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

#ifndef MENUCUSTOMLOADSKIRMISHMULTI_H
#define MENUCUSTOMLOADSKIRMISHMULTI_H

#include "dynamiccast.h"
#include "menucustombase.h"
#include "menuloadskirmishmulti.h"
#include <RoomsPlugin.h>

namespace hooks {

class CMenuCustomLoadSkirmishMulti
    : public game::CMenuLoadSkirmishMulti
    , public CMenuCustomBase
{
public:
    static constexpr char dialogName[] = "DLG_LOAD_SKIRMISH";
    static CMenuCustomLoadSkirmishMulti* cast(CMenuBase* menu);

    CMenuCustomLoadSkirmishMulti(game::CMenuPhase* menuPhase);
    ~CMenuCustomLoadSkirmishMulti();

    void createRoomAndServer();

protected:
    static game::RttiInfo<game::CMenuLoadSkirmishMultiVftable> rttiInfo;

    // CInterface
    static void __fastcall destructor(CMenuCustomLoadSkirmishMulti* thisptr,
                                      int /*%edx*/,
                                      char flags);

    class RoomsCallback : public SLNet::RoomsCallback
    {
    public:
        RoomsCallback(CMenuCustomLoadSkirmishMulti* menu)
            : m_menu{menu}
        { }

        void CreateRoom_Callback(const SLNet::SystemAddress& senderAddress,
                                 SLNet::CreateRoom_Func* callResult) override;

    private:
        CMenuCustomLoadSkirmishMulti* m_menu;
    };

private:
    RoomsCallback m_roomsCallback;
};

assert_offset(CMenuCustomLoadSkirmishMulti, CMenuLoad::CMenuBase::vftable, 0);

} // namespace hooks

#endif // MENUCUSTOMLOADSKIRMISHMULTI_H
