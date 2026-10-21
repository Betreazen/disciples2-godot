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

#ifndef EDITBOXINTERF_H
#define EDITBOXINTERF_H

#include "d2set.h"
#include "d2string.h"
#include "interface.h"
#include "mqpoint.h"
#include "uievent.h"

namespace game {

struct CEditBoxFocus;
struct CImage2Fill;
struct CImage2TextBackground;
struct CDialogInterf;

enum class EditFilter : int
{
    NoFilter,        /**< Any input allowed. */
    TextOnly,        /**< Only text characters, punctuation marks are not allowed. */
    AlphaNum,        /**< Text, digits, punctuation marks, some special characters. */
    DigitsOnly,      /**< Only digits. */
    AlphaNumNoSlash, /**< Text, digits, some punctuation marks. '\', '/' not allowed. */
    NamesDot,        /**< Same as EditFilter::Names, but allows punctuation marks. */
    Names,           /**< Only characters suitable for Windows file names. */
};

struct EditBoxData
{
    EditFilter filter;
    SmartPointer ptr;
    int maxInputLength; /**< Allowed range is [1 : 4096]. */
    String formatString;
    String inputString;
    std::uint32_t textCursorPosIdx;
    union
    {
        struct
        {
            bool allowEnter;
            char padding[3];
        } original;
        struct
        {
            bool allowEnter;
            bool isPassword;
            char padding[2];
        } patched;
    };
};

assert_size(EditBoxData, 56);

struct CEditBoxInterfData
{
    SmartPtr<CEditBoxFocus> editBoxFocus;
    int textBoxChildIndex;
    // Differs from enabled because the control can still receive input focus
    bool editable;
    char padding[3];
    EditBoxData editBoxData;
    CImage2Fill* textCursor;
    CMqPoint textCursorPos;
    int textCursorHeight;
    CImage2TextBackground* background;
    CMqPoint bgndImagePos;
};

assert_size(CEditBoxInterfData, 100);

/**
 * Edit box ui element.
 * Represents EDIT from Interf.dlg or ScenEdit.dlg files.
 */
struct CEditBoxInterf : public CInterface
{
    CEditBoxInterfData* data;
};

assert_size(CEditBoxInterf, 12);

struct CEditBoxFocus
{
    Set<CInterface*> dialogs;
    CInterface* unknown28;
    /**
     * Has timeout of 200ms.
     * Increments cursor counter until it reaches 2, then drops it to -3 and continues.
     */
    UiEvent cursorBlinkEvent;
    /**
     * Being reset to 0 on mouse click or keyboard input to a focused edit box.
     * Assumption: the cursor is hidden while the counter is negative.
     */
    int cursorBlinkCounter;
};

assert_size(CEditBoxFocus, 60);
assert_offset(CEditBoxFocus, unknown28, 28);

namespace CEditBoxInterfApi {

struct Api
{
    /**
     * Searches for specified edit box and sets its filter and max input length.
     * @returns found edit box.
     */
    using SetFilterAndLength = CEditBoxInterf*(__stdcall*)(CDialogInterf* dialog,
                                                           const char* controlName,
                                                           const char* dialogName,
                                                           EditFilter filter,
                                                           int inputLength);
    SetFilterAndLength setFilterAndLength;

    /**
     * Sets max input length of edit box.
     * Updates edit box state after changing length.
     */
    using SetInputLength = void(__thiscall*)(CEditBoxInterf* thisptr, int length);
    SetInputLength setInputLength;

    using SetString = void(__thiscall*)(CEditBoxInterf* thisptr, const char* string);
    SetString setString;

    using Update = void(__thiscall*)(CEditBoxInterf* thisptr);
    Update update;

    using IsCharValid = bool(__thiscall*)(const EditBoxData* thisptr, char ch);
    IsCharValid isCharValid;

    using EditBoxDataCtor = EditBoxData*(__thiscall*)(EditBoxData* thisptr);
    EditBoxDataCtor editBoxDataCtor;

    using GetTextCursorPosIdx = std::uint32_t(__thiscall*)(EditBoxData* thisptr);
    GetTextCursorPosIdx getTextCursorPosIdx;

    using SetEditable = void(__thiscall*)(CEditBoxInterf* thisptr, bool value);
    SetEditable setEditable;

    using SetFocus = void(__thiscall*)(CEditBoxInterf* thisptr);
    SetFocus setFocus;

    /** Resets blink counter to 0 making cursor visible for a while. */
    using ResetCursorBlink = void(__thiscall*)(CEditBoxFocus* thisptr);
    ResetCursorBlink resetCursorBlink;

    using IsFocused = bool(__thiscall*)(CEditBoxFocus* thisptr, const CEditBoxInterf* editBox);
    IsFocused isFocused;

    using SetFocused = void(__thiscall*)(CEditBoxFocus* thisptr, const CEditBoxInterf* editBox);
    SetFocused setFocused;
    SetFocused focusNext;
    SetFocused focusPrev;
};

Api& get();

} // namespace CEditBoxInterfApi

} // namespace game

#endif // EDITBOXINTERF_H
