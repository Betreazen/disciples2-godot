/*
 * This file is part of the modding toolset for Disciples 2.
 * (https://github.com/VladimirMakeev/D2ModdingToolset)
 * Copyright (C) 2020 Vladimir Makeev.
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

#ifndef MIDGARDMSGBOX_H
#define MIDGARDMSGBOX_H

#include "popupdialoginterf.h"
#include "uievent.h"

namespace game {

struct CMidMsgBoxButtonHandler;
struct CMidMsgBoxUpdateHandler;

struct CMidgardMsgBoxData
{
    CDialogInterf* dialogInterf;
    UiEvent updateEvent;
    int updateCounter;
    int unknown;
    bool okButtonOnly;
    char padding[3];
    CMidMsgBoxButtonHandler* buttonHandler;
    CMidMsgBoxUpdateHandler* updateHandler;
};

assert_size(CMidgardMsgBoxData, 48);

/**
 * Message box ui element.
 * By default, represents DLG_MESSAGE_BOX from Interf.dlg.
 */
struct CMidgardMsgBox : public CPopupDialogInterf
{
    CMidgardMsgBoxData* data;
};

assert_size(CMidgardMsgBox, 20);
assert_offset(CMidgardMsgBox, data, 16);

namespace CMidgardMsgBoxApi {

struct Api
{
    /**
     * Creates message box.
     * @param[in] thisptr message box to initialize.
     * @param[in] message text to show.
     * @param showYesNoButtons if set to 1, creates message box with 'BTN_YES' and 'BTN_NO' buttons
     * instead of 'BTN_OK'.
     * @param[in] buttonHandler handler logic to execute upon message box closing.
     * Handler object destroyed and its memory freed in message box destructor.
     * @param[in] updateHandler handler to call when update UIEvent is triggered 10 times. Seems to
     * be unused (nullptr) in the game code.
     * @param[in] dialogName name of the custom dialog ui element to show instead of default,
     * or nullptr. Default dialog is 'DLG_MESSAGE_BOX' from Interf.dlg. Seems to be unused (nullptr)
     * in the game code.
     */
    using Constructor = CMidgardMsgBox*(__thiscall*)(CMidgardMsgBox* thisptr,
                                                     const char* message,
                                                     int showYesNoButtons,
                                                     CMidMsgBoxButtonHandler* buttonHandler,
                                                     CMidMsgBoxUpdateHandler* updateHandler,
                                                     const char* dialogName);
    Constructor constructor;
};

Api& get();

} // namespace CMidgardMsgBoxApi

} // namespace game

#endif // MIDGARDMSGBOX_H
