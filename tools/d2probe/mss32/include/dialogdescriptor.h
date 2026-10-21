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

#ifndef DIALOGDESCRIPTOR_H
#define DIALOGDESCRIPTOR_H

#include "d2assert.h"
#include "mqrect.h"

namespace game {

struct CAutoDialog;

struct DialogDescriptor
{
    CAutoDialog* autoDialog;
    char name[48];
    CMqRect rect;
    CMqRect clipRect;
    char backgroundName[48];
    bool isAutoBackground;
    char padding[3];
    int cursorType;
    int unknown140; // Always 0
    char cursorName[48];
    char childs[36]; // Unknown key-value container
};

assert_size(DialogDescriptor, 228);
assert_offset(DialogDescriptor, unknown140, 140);

namespace DialogDescriptorApi {

struct Api
{
    using Constructor = DialogDescriptor*(__thiscall*)(DialogDescriptor* thisptr,
                                                       CAutoDialog* autoDialog,
                                                       const char* name);
    Constructor constructor;

    using Destructor = void(__thiscall*)(DialogDescriptor* thisptr);
    Destructor destructor;
};

Api& get();

} // namespace DialogDescriptorApi

} // namespace game

#endif // DIALOGDESCRIPTOR_H
