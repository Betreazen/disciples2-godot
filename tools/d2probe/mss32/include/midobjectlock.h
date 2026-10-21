/*
 * This file is part of the modding toolset for Disciples 2.
 * (https://github.com/VladimirMakeev/D2ModdingToolset)
 * Copyright (C) 2022 Vladimir Makeev.
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

#ifndef MIDOBJECTLOCK_H
#define MIDOBJECTLOCK_H

#include "autoptr.h"
#include "functordispatch0.h"
#include "midcommandqueue2.h"
#include "middatacache.h"
#include <cstdint>

namespace game {

struct CMidObjectLock : public CMidDataCache2::INotify
{
    struct Notify1 : public CMidCommandQueue2::INotifyCQ
    {
        CMidCommandQueue2* commandQueue;
        /** Increments CMidObjectLock::pendingLocalUpdates */
        SmartPtr<CBFunctorDispatch0> notifyCallback;
    };

    struct Notify2 : public CMidCommandQueue2::INotifyCQ
    {
        CMidCommandQueue2* commandQueue;
        /** Decrements CMidObjectLock::pendingLocalUpdates */
        SmartPtr<CBFunctorDispatch0> notifyCallback;
    };

    CMidCommandQueue2* commandQueue;
    CMidDataCache2* dataCache;
    AutoPtr<Notify1> notify1;
    AutoPtr<Notify2> notify2;
    /**
     * Assumption: incremented at the start of CMidCommandQueue2::notifyList processing
     * and decremented at the end.
     */
    std::uint32_t pendingLocalUpdates;
    /**
     * Incremented each time the client sends network message to a server.
     * See calls of CPhaseGameIncrementNetworkUpdates.
     * Set to 0 on any call to CMidDataCache2::INotify::OnObjectChanged.
     */
    std::uint32_t pendingNetworkUpdates;
    union
    {
        struct
        {
            /** True when CTaskAskExportLeader is in process. */
            bool exportingLeader;
            char padding[3];
        } original;
        struct
        {
            bool exportingLeader;
            /**
             * Prevents possible client desync while server is processing all effects for
             * CStackMoveMsg, meanwhile sending separate messages like CCmdMoveStackMsg,
             * CCmdBattleStartMsg, etc.
             * Set on sending CStackMoveMsg to server.
             * Reset on receiving CCmdMoveStackEndMsg back.
             */
            bool movingStack;
            char padding[2];
        } patched;
    };
};

assert_size(CMidObjectLock, 32);
assert_size(CMidObjectLock::Notify1, 16);
assert_size(CMidObjectLock::Notify2, 16);

namespace CMidObjectLockApi {

struct Api
{
    using Constructor = CMidObjectLock*(__thiscall*)(CMidObjectLock* thisptr,
                                                     CMidCommandQueue2* commandQueue,
                                                     CMidDataCache2* dataCache);
    Constructor constructor;

    using Destructor = void(__thiscall*)(CMidObjectLock* thisptr);
    Destructor destructor;

    using NotifyCQCallback = void(__thiscall*)(CMidObjectLock* thisptr);
    NotifyCQCallback notify1Callback;
    NotifyCQCallback notify2Callback;
};

Api& get();

CMidDataCache2::INotifyVftable* vftable();

} // namespace CMidObjectLockApi

} // namespace game

#endif // MIDOBJECTLOCK_H
