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

#include "editboxinterfhooks.h"
#include "formattedtext.h"
#include "originalfunctions.h"
#include "textboxinterf.h"
#include <string>

namespace hooks {

void __fastcall editBoxInterfUpdateHooked(game::CEditBoxInterf* thisptr, int /*%edx*/)
{
    using namespace game;

    getOriginalFunctions().editBoxInterfUpdate(thisptr);

    auto* data = thisptr->data;
    if (data->editBoxData.patched.isPassword) {
        auto textBox = (CTextBoxInterf*)thisptr->vftable->getChild(thisptr,
                                                                   &data->textBoxChildIndex);
        if (textBox) {
            std::string mask(data->editBoxData.inputString.length, '*');
            CTextBoxInterfApi::get().setString(textBox, mask.c_str());

            auto cursorPosPtr = mask.c_str();
            auto cursorPosIdx = data->editBoxData.textCursorPosIdx;
            if (cursorPosIdx <= mask.length()) {
                cursorPosPtr += cursorPosIdx;
            }
            auto& cursorPos = data->textCursorPos;
            auto editBoxArea = thisptr->vftable->getArea(thisptr);

            FormattedTextPtr ptr;
            IFormattedTextApi::get().getFormattedText(&ptr);
            ptr.data->vftable->getTextCursorPos(ptr.data, &cursorPos, mask.c_str(), cursorPosPtr,
                                                editBoxArea);
            SmartPointerApi::get().createOrFree((SmartPointer*)&ptr, nullptr);

            // See CEditBoxInterf::Update
            if (cursorPos.x <= 0 || cursorPos.y <= 0) {
                cursorPos.x = editBoxArea->left;
                cursorPos.y = editBoxArea->top;
            } else {
                cursorPos.y -= data->textCursorHeight;
            }
        }
    }
}

game::EditBoxData* __fastcall editBoxDataCtorHooked(game::EditBoxData* thisptr, int /*%edx*/)
{
    getOriginalFunctions().editBoxDataCtor(thisptr);
    thisptr->patched.isPassword = false;
    return thisptr;
}

} // namespace hooks
