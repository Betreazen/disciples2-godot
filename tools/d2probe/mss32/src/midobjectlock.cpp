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

#include "midobjectlock.h"
#include "version.h"
#include <array>

namespace game::CMidObjectLockApi {

// clang-format off
static std::array<Api, 4> functions = {{
    // Akella
    Api{
        (Api::Constructor)0x417d8d,
        (Api::Destructor)0x41806e,
        (Api::NotifyCQCallback)0x418101,
        (Api::NotifyCQCallback)0x41810c,
    },
    // Russobit
    Api{
        (Api::Constructor)0x417d8d,
        (Api::Destructor)0x41806e,
        (Api::NotifyCQCallback)0x418101,
        (Api::NotifyCQCallback)0x41810c,
    },
    // Gog
    Api{
        (Api::Constructor)0x417979,
        (Api::Destructor)0x417c53,
        (Api::NotifyCQCallback)0x417ce6,
        (Api::NotifyCQCallback)0x417cf1,
    },
    // Scenario Editor
    Api{
        (Api::Constructor)nullptr,
        (Api::Destructor)nullptr,
        (Api::NotifyCQCallback)nullptr,
        (Api::NotifyCQCallback)nullptr,
    },
}};

static std::array<CMidDataCache2::INotifyVftable*, 4> vftables = {{
    // Akella
    (CMidDataCache2::INotifyVftable*)0x6cf97c,
    // Russobit
    (CMidDataCache2::INotifyVftable*)0x6cf97c,
    // Gog
    (CMidDataCache2::INotifyVftable*)0x6cd91c,
    // Scenario Editor
    (CMidDataCache2::INotifyVftable*)nullptr,
}};
// clang-format on

Api& get()
{
    return functions[static_cast<int>(hooks::gameVersion())];
}

CMidDataCache2::INotifyVftable* vftable()
{
    return vftables[static_cast<int>(hooks::gameVersion())];
}

} // namespace game::CMidObjectLockApi
