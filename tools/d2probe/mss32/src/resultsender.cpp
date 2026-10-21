/*
 * This file is part of the modding toolset for Disciples 2.
 * (https://github.com/VladimirMakeev/D2ModdingToolset)
 * Copyright (C) 2025 Alexey Voskresensky.
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

#include "resultsender.h"
#include "version.h"
#include <array>

namespace game::CResultSenderApi {

static std::array<const void*, 3> vftables = {{
    // Akella
    (const void*)0x6d05fc,
    // Russobit
    (const void*)0x6d05fc,
    // Gog
    (const void*)0x6ce59c,
}};
// clang-format on

const void* vftable()
{
    return vftables[static_cast<int>(hooks::gameVersion())];
}

} // namespace game::CResultSenderApi
