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

#ifndef MENULOAD_H
#define MENULOAD_H

#include "catalogvalidate.h"
#include "d2string.h"
#include "idlist.h"
#include "menubase.h"
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

namespace game {

struct IGameCatalog;
struct NetMsgEntryData;
struct CMenuLoadData;
struct CWaitInterf;
struct CScensCatalog;

/** Base class for all menus that loads previously saved game. */
struct CMenuLoad
    : public CMenuBase
    , public ICatalogValidate
{
    CMenuLoadData* menuLoadData;
};

assert_offset(CMenuLoad, ICatalogValidate::vftable, 12);

struct CMenuLoadVftable : public CMenuBaseVftable
{
    void* methods[3];

    using SetGameName = void(__thiscall*)(CMenuLoad* thisptr, const char* value);
    SetGameName setGameName;

    using GetGameName = const char*(__thiscall*)(const CMenuLoad* thisptr);
    GetGameName getGameName;

    using GetPassword = const char*(__thiscall*)(const CMenuLoad* thisptr);
    GetPassword getPassword;

    using GetPlayerName = const char*(__thiscall*)(const CMenuLoad* thisptr);
    GetPlayerName getPlayerName;

    using CreateScensCatalog = CScensCatalog*(__thiscall*)(const CMenuLoad* thisptr,
                                                           ICatalogValidate* catalogValidate);
    CreateScensCatalog createScensCatalog;

    using GetScenarioFileHandle = HANDLE(__thiscall*)(const CMenuLoad* thisptr,
                                                      String* resultPath,
                                                      CScensCatalog* scensCatalog,
                                                      int selectedIndex);
    GetScenarioFileHandle getScenarioFileHandle;
};
assert_vftable_size(CMenuLoadVftable, 44);

struct CMenuLoadData
{
    IGameCatalog* gameCatalog;
    NetMsgEntryData** netMsgEntryData;
    CWaitInterf* waitInterf;
    /** For some reason, CreateServer function is called on timer with 100ms timeout. */
    UiEvent createServerEvent;
    char unknown24;
    /** Determines whether the fullscreen hourglass interface should be used. */
    bool useWaitInterf;
    char unknown26;
    char unknown27;
    String scenarioFilePath;
    HANDLE handle;
    char scenarioFileHeader[2893];
    char padding[3];
    IdList list;
    int unknownB9C;
    IdListNode* listNode;
    void* unknownBA4;
    bool unknownBA8;
    char unknownBA9[3];
    int unknownBAC;
};

assert_size(CMenuLoadData, 2992);

namespace CMenuLoadApi {

struct Api
{
    using ButtonCallback = void(__thiscall*)(CMenuLoad* thisptr);
    /** Loads selected game save file. */
    ButtonCallback buttonLoadCallback;

    /** Creates host player client and requests game version from server. */
    using CreateHostPlayer = void(__thiscall*)(CMenuLoad* thisptr);
    CreateHostPlayer createHostPlayer;

    using CreateServer = void(__thiscall*)(CMenuLoad* thisptr);
    CreateServer createServer;
};

Api& get();

} // namespace CMenuLoadApi

} // namespace game

#endif // MENULOAD_H
