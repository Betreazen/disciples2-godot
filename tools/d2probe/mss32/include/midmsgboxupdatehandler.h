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

#ifndef MIDMSGBOXUPDATEHANDLER_H
#define MIDMSGBOXUPDATEHANDLER_H

#include "d2assert.h"

namespace game {

struct CMidMsgBoxUpdateHandlerVftable;
struct CMidgardMsgBox;

struct CMidMsgBoxUpdateHandler
{
    CMidMsgBoxUpdateHandlerVftable* vftable;
};

assert_size(CMidMsgBoxUpdateHandler, 4);

struct CMidMsgBoxUpdateHandlerVftable
{
    using Destructor = void(__thiscall*)(CMidMsgBoxUpdateHandler* thisptr);
    Destructor destructor;

    /**
     * CMidgardMsgBox update handler function.
     * Called when update UIEvent is triggered 10 times (see CMidgardMsgBox::onVisibilityChanged).
     */
    using Handler = void(__thiscall*)(CMidMsgBoxUpdateHandler* thisptr, CMidgardMsgBox* msgBox);
    Handler handler;
};

assert_vftable_size(CMidMsgBoxUpdateHandlerVftable, 2);

} // namespace game

#endif // MIDMSGBOXUPDATEHANDLER_H
