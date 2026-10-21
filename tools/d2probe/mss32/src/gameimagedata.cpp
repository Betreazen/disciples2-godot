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

#include "gameimagedata.h"
#include "version.h"
#include <array>

namespace game::GameImageDataApi {

// clang-format off
static std::array<Api, 4> functions = {{
    // Akella
    Api{
        (Api::Destructor)0x5239c6,
        (Api::LoadFromFile)0x5239dc,
    },
    // Russobit
    Api{
        (Api::Destructor)0x5239c6,
        (Api::LoadFromFile)0x5239dc,
    },
    // Gog
    Api{
        (Api::Destructor)0x522e68,
        (Api::LoadFromFile)0x522e7e,
    },
    // Scenario Editor
    Api{
        (Api::Destructor)0x4b90d2,
        (Api::LoadFromFile)0x4b90e8,
    },
}};
// clang-format on

Api& get()
{
    return functions[static_cast<int>(hooks::gameVersion())];
}

} // namespace game::GameImageDataApi
