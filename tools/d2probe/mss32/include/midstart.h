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

#ifndef MIDSTART_H
#define MIDSTART_H

#include "uievent.h"

namespace game {

struct CProxyCodeBaseEnv;

struct CMidStart
{
    unsigned int loadGlobalMsgId;
    UiEvent loadGlobalEvent;
    bool allowLoad;
    char padding[3];
    char* argument;
    bool usingGameSpy;
    char padding2[3];
    CProxyCodeBaseEnv* proxyCodeBaseEnv;
    int unknown44;
};

assert_size(CMidStart, 48);
assert_offset(CMidStart, unknown44, 44);

namespace CMidStartApi {

struct Api
{
    using Constructor = CMidStart*(__thiscall*)(CMidStart* thisptr,
                                                char* argument,
                                                bool usingGameSpy);
    Constructor constructor;

    using Destructor = void(__thiscall*)(CMidStart* thisptr);
    Destructor destructor;
};

Api& get();

} // namespace CMidStartApi

} // namespace game

#endif // MIDSTART_H
