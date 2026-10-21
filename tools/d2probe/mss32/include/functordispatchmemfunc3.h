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

#ifndef FUNCTORDISPATCHMEMFUNC3_H
#define FUNCTORDISPATCHMEMFUNC3_H

namespace game {

template <typename T, typename U, typename V>
struct CBFunctorDispatchMemFunc3Vftable;

template <typename T, typename U, typename V>
struct CBFunctorDispatchMemFunc3
{
    CBFunctorDispatchMemFunc3Vftable<T, U, V>* vftable;
};

template <typename T, typename U, typename V>
struct CBFunctorDispatchMemFunc3Vftable
{
    /** Calls functor-specific callback function, passing it T, U and V as a parameters. */
    using RunCallback = void(__thiscall*)(CBFunctorDispatchMemFunc3<T, U, V>* thisptr,
                                          T value,
                                          U value2,
                                          V value3);
    RunCallback runCallback;
};

} // namespace game

#endif // FUNCTORDISPATCHMEMFUNC3_H
