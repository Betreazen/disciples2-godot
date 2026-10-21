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

#include "menuloadhooks.h"
#include "menucustomloadskirmishmulti.h"
#include "originalfunctions.h"
#include "uievent.h"

namespace hooks {

void __fastcall menuLoadCreateServerHooked(game::CMenuLoad* thisptr)
{
    using namespace game;

    auto custom = CMenuCustomLoadSkirmishMulti::cast(thisptr);
    if (custom) {
        // Stop the event to prevent multiple calls while waiting for the room
        UiEventApi::get().release(&custom->menuLoadData->createServerEvent);
        custom->createRoomAndServer();
        return;
    }

    getOriginalFunctions().menuLoadCreateServer(thisptr);
}

} // namespace hooks
