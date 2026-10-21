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

#ifndef AUTODIALOGFILE_H
#define AUTODIALOGFILE_H

#include "d2string.h"
#include "stringarray.h"

namespace game {

/**
 * Stores AutoDialog script (.dlg) information.
 */
struct CAutoDialogFile
{
    bool readMode; /**< If false, writes lines to script file on destruction. */
    char padding[3];
    int currentLineIdx; /**< Current line index for parser. */
    StringArray lines;  /**< Script file text lines. */
    String scriptPath;  /**< Full path to script (.dlg) file. */
};

assert_size(CAutoDialogFile, 40);

namespace AutoDialogFileApi {

struct Api
{
    /**
     * Loads AutoDialog script file and populates CAutoDialogFile with its contents.
     * @param[in] thisptr pointer to CAutoDialogFile where to store the data.
     * @param[in] filePath full path to script file.
     * @param unused
     * @returns thisptr.
     */
    using Constructor = CAutoDialogFile*(__thiscall*)(CAutoDialogFile* thisptr,
                                                      const char* filePath,
                                                      int /*unused*/);
    Constructor constructor;

    using Destructor = void(__thiscall*)(CAutoDialogFile* thisptr);
    Destructor destructor;
};

Api& get();

} // namespace AutoDialogFileApi

} // namespace game

#endif // AUTODIALOGFILE_H
