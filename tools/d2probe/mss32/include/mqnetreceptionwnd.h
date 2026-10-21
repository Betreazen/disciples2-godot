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

#ifndef MQNETRECEPTIONWND_H
#define MQNETRECEPTIONWND_H

#include "mqnetreception.h"
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

namespace game {

/**
 * It has a single notify() method that only does
 * PostMessage(this->window, this->messageId, this->wParam, this->lParam)
 */
struct CMqNetReceptionWnd : IMqNetReception
{
    /** Either "MQ_UIManager" (main window) or "ThreadWindowClass" (server thread). */
    HWND window;
    /** Either "MIDGARD NETMSG" (CMidgardData::netMessageId) or "CMidServer"
     * (CMidServerData::serverMessageId).
     */
    UINT messageId;
    /** Always 0. */
    WPARAM wParam;
    /** Stores player netId. See IMqNetPlayerVftable::getNetId. */
    LPARAM lParam;
};

} // namespace game

#endif // MQNETRECEPTIONWND_H
