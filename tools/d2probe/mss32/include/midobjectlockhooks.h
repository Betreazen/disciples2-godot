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

#ifndef MIDOBJECTLOCKHOOKS_H
#define MIDOBJECTLOCKHOOKS_H

namespace game {
struct CMidObjectLock;
struct CMidCommandQueue2;
struct CMidDataCache2;
struct IMidScenarioObject;
} // namespace game

namespace hooks {

game::CMidObjectLock* __fastcall midObjectLockCtorHooked(game::CMidObjectLock* thisptr,
                                                         int /*%edx*/,
                                                         game::CMidCommandQueue2* commandQueue,
                                                         game::CMidDataCache2* dataCache);

void __fastcall midObjectLockNotify1CallbackHooked(game::CMidObjectLock* thisptr, int /*%edx*/);

void __fastcall midObjectLockNotify2CallbackHooked(game::CMidObjectLock* thisptr, int /*%edx*/);

void __fastcall midObjectLockOnObjectChangedHooked(game::CMidObjectLock* thisptr,
                                                   int /*%edx*/,
                                                   const game::IMidScenarioObject* obj);

} // namespace hooks

#endif // MIDOBJECTLOCKHOOKS_H
