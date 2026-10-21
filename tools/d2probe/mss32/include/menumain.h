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

#ifndef MENUMAIN_H
#define MENUMAIN_H

#include "menubase.h"

namespace game {

struct CMenuMainData;

struct CMenuMain : public CMenuBase
{
    CMenuMainData* data;
};

assert_size(CMenuMain, 16);

struct CMenuMainData
{
    int unknown;
};

assert_size(CMenuMainData, 4);

namespace CMenuMainApi {

struct Api
{
    using Constructor = CMenuMain*(__thiscall*)(CMenuMain* thisptr, CMenuPhase* menuPhase);
    Constructor constructor;

    using Destructor = void(__thiscall*)(CMenuMain* thisptr);
    Destructor destructor;

    using CreateMenu = CMenuMain*(__stdcall*)(CMenuPhase* menuPhase);
    CreateMenu createMenu;
};

Api& get();

} // namespace CMenuMainApi

} // namespace game

#endif // MENUMAIN_H
