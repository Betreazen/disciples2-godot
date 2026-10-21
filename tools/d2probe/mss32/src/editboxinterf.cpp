/*
 * This file is part of the modding toolset for Disciples 2.
 * (https://github.com/VladimirMakeev/D2ModdingToolset)
 * Copyright (C) 2021 Vladimir Makeev.
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

#include "editboxinterf.h"
#include "version.h"
#include <array>

namespace game::CEditBoxInterfApi {

// clang-format off
static std::array<Api, 4> functions = {{
    // Akella
    Api{
        (Api::SetFilterAndLength)0x5c9c86,
        (Api::SetInputLength)0x5390dc,
        (Api::SetString)0x5391f9,
        (Api::Update)0x539486,
        (Api::IsCharValid)0x538471,
        (Api::EditBoxDataCtor)0x5377aa,
        (Api::GetTextCursorPosIdx)0x5378a0,
        (Api::SetEditable)0x539104,
        (Api::SetFocus)0x539139,
        (Api::ResetCursorBlink)0x5389e6,
        (Api::IsFocused)0x538c48,
        (Api::SetFocused)0x538cde,
        (Api::SetFocused)0x538d5a,
        (Api::SetFocused)0x538e0e,
    },
    // Russobit
    Api{
        (Api::SetFilterAndLength)0x5c9c86,
        (Api::SetInputLength)0x5390dc,
        (Api::SetString)0x5391f9,
        (Api::Update)0x539486,
        (Api::IsCharValid)0x538471,
        (Api::EditBoxDataCtor)0x5377aa,
        (Api::GetTextCursorPosIdx)0x5378a0,
        (Api::SetEditable)0x539104,
        (Api::SetFocus)0x539139,
        (Api::ResetCursorBlink)0x5389e6,
        (Api::IsFocused)0x538c48,
        (Api::SetFocused)0x538cde,
        (Api::SetFocused)0x538d5a,
        (Api::SetFocused)0x538e0e,
    },
    // Gog
    Api{
        (Api::SetFilterAndLength)0x5c8c54,
        (Api::SetInputLength)0x5386e4,
        (Api::SetString)0x538801,
        (Api::Update)0x538a8e,
        (Api::IsCharValid)0x537a79,
        (Api::EditBoxDataCtor)0x536da5,
        (Api::GetTextCursorPosIdx)0x536e9b,
        (Api::SetEditable)0x53870c,
        (Api::SetFocus)0x538741,
        (Api::ResetCursorBlink)0x537fee,
        (Api::IsFocused)0x538250,
        (Api::SetFocused)0x5382e6,
        (Api::SetFocused)0x538362,
        (Api::SetFocused)0x538416,
    },
    // Scenario Editor
    Api{
        (Api::SetFilterAndLength)0x4d18b8,
        (Api::SetInputLength)0x492cb3,
        (Api::SetString)0x492ddb,
        (Api::Update)0x493068,
        (Api::IsCharValid)0x492048,
        (Api::EditBoxDataCtor)0x491340,
        (Api::GetTextCursorPosIdx)0x491436,
        (Api::SetEditable)0x492cdb,
        (Api::SetFocus)0x492d09,
        (Api::ResetCursorBlink)0x4925bd,
        (Api::IsFocused)0x49281f,
        (Api::SetFocused)0x4928b5,
        (Api::SetFocused)0x492931,
        (Api::SetFocused)0x4929e5,
    },
}};
// clang-format on

Api& get()
{
    return functions[static_cast<int>(hooks::gameVersion())];
}

} // namespace game::CEditBoxInterfApi
