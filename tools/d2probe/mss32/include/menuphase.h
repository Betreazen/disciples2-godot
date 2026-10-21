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

#ifndef MENUPHASE_H
#define MENUPHASE_H

#include "catalogvalidate.h"
#include "difficultylevel.h"
#include "midgardid.h"
#include "mqnetsystem.h"
#include "racelist.h"
#include "smartptr.h"
#include <cstddef>
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

namespace game {

struct CMidgard;
struct CInterfManagerImpl;
struct ScenarioDataArrayWrapped;
struct IMqImage2;
struct CMenuBase;

template <typename T>
struct CInterfaceT;
struct CInterfaceVftable;
using CInterface = CInterfaceT<CInterfaceVftable>;

/**
 * Used by CMenuPhase::SwitchPhase as phase transition command.
 * Entity names generally correspond to CMenu<Name>.
 */
enum class MenuTransition : int
{
    Main2Single = 0,
    Main2Protocol = 1,
    Main2Intro = 2,
    Main2Credits = 3,
    Main2CustomLobby = 4,
    Single2RaceCampaign = 0,
    Single2LoadCampaign = 1,
    Single2NewSkirmish = 2,
    Single2LoadSkirmish = 3,
    Single2CustomCampaign = 4,
    Single2LoadCustomCampaign = 5,
    Protocol2Hotseat = 0,
    Protocol2Multi = 1,
    Hotseat2NewSkirmishHotseat = 0,
    Hotseat2LoadSkirmishHotseat = 1,
    Multi2NewSkirmish = 0,
    Multi2Session = 1,
    Multi2LoadSkirmish = 2,
    RaceCampaign2LordHu = 0,
    RaceCampaign2LordHe = 1,
    RaceCampaign2LordDw = 2,
    RaceCampaign2LordEl = 3,
    RaceCampaign2LordUn = 4,
    RaceSkirmish2LordHu = RaceCampaign2LordHu,
    RaceSkirmish2LordHe = RaceCampaign2LordHe,
    RaceSkirmish2LordDw = RaceCampaign2LordDw,
    RaceSkirmish2LordEl = RaceCampaign2LordEl,
    RaceSkirmish2LordUn = RaceCampaign2LordUn,
    Session2LobbyJoin = 0,
    Unknown2NewSkirmish = 0,
    Unknown2LobbyHost = 1,
    Unknown2LobbyJoin = 1,
    NewSkirmish2RaceSkirmish = 0,
    NewSkirmishMulti2LobbyHost = NewSkirmish2RaceSkirmish,
    NewSkirmishSingle2RandomScenarioSingle = 1,
    NewSkirmishMulti2RandomScenarioMulti = 2,
    NewSkirmishHotseat2HotseatLobby = 0,
    NewSkirmishHotseat2RandomScenarioHotseat = NewSkirmishSingle2RandomScenarioSingle,
    RandomScenarioSingle2RaceCampaign = 0,
    RandomScenarioMulti2LobbyHost = 0,
    CustomLobby2NewSkirmish = 0,
    CustomLobby2LoadSkirmish = 1,
    CustomLobby2LobbyJoin = 2,
};

/**
 * Used by CMenuPhase to indicate displayed menu phases.
 * Entity names generally correspond to CMenu<Name>.
 */
enum class MenuPhase : int
{
    Main = 0,
    Main2Intro = 17,
    Main2Credits = 18,
    Main2Single = 22,
    Main2Proto = 23,
    Main2CustomLobby = 35,

    Single = 1,
    Single2RaceCampaign = 26,
    Single2LoadCampaign = 34,
    Single2NewSkirmish = 27,
    Single2LoadSkirmish = 33,
    Single2CustomCampaign = 29,
    Single2LoadCustomCampaign = 32,

    Protocol = 2,
    Protocol2Hotseat = 24,
    Protocol2Multi = 25,

    Hotseat = 3,
    Hotseat2NewSkirmishHotseat = 9,
    Hotseat2LoadSkirmishHotseat = 10,

    Multi = 4,
    Multi2NewSkirmish = Single2NewSkirmish,
    Multi2Session = 11,
    Multi2LoadSkirmish = Single2LoadSkirmish,

    RaceCampaign = 5,
    RaceCampaign2Lord = 31,

    // Common phase for different menus depending on values from CMidgardData and CMenuPhaseData
    NewSkirmishSingle = 6,
    NewSkirmishMulti = NewSkirmishSingle,
    NewSkirmishMultiLobby = NewSkirmishSingle,
    NewSkirmishGameSpy = NewSkirmishSingle,
    NewSkirmish2RaceSkirmish = 28,
    NewSkirmish2LobbyHost = 15,

    RaceSkirmish = 7,
    RaceSkirmish2Lord = RaceCampaign2Lord,

    CustomCampaign = 8,
    CustomCampaign2RaceSkirmish = NewSkirmish2RaceSkirmish,

    Session = 12,
    Session2LobbyJoin = NewSkirmish2LobbyHost,

    // TODO: determine unknown phase
    Unknown = 13,
    Unknown2NewSkirmish = Single2NewSkirmish,
    Unknown2LobbyHost = 15,
    Unknown2LobbyJoin = Unknown2LobbyHost,

    // Common phase for different menus depending on CMenuPhaseData::useGameSpy/loadScenario
    LoadSkirmishMulti = 15,
    LoadSkirmishGameSpy = LoadSkirmishMulti,

    // Common phase for different menus depending on previous phase and CMenuPhaseData
    Lord = 16,
    LoadCampaign = Lord,
    LoadCustomCampaign = Lord,
    LoadSkirmishSingle = Lord,
    LoadSkirmishHotseat = Lord,
    HotseatLobby = Lord,
    LobbyHost = Lord,
    LobbyJoin = Lord,
    WaitInterf = Lord,

    Credits = 19,
    Credits2Main = 20,

    Back2Main = 21,

    NewSkirmishHotseat = 30,
    NewSkirmishHotseat2HotseatLobby = 14,

    CustomLobby = 36,

    Back2CustomLobby = 37,

    NewSkirmishSingle2RandomScenarioSingle = 38,
    RandomScenarioSingle = 39,

    NewSkirmishHotseat2RandomScenarioHotseat = 40,
    RandomScenarioHotseat = 41,

    NewSkirmishMulti2RandomScenarioMulti = 42,
    RandomScenarioMulti = 43,

    Last = RandomScenarioMulti,
};

assert_enum_value(MenuPhase, Main, 0);
assert_enum_value(MenuPhase, Single, 1);
assert_enum_value(MenuPhase, Protocol, 2);
assert_enum_value(MenuPhase, Hotseat, 3);
assert_enum_value(MenuPhase, Multi, 4);
assert_enum_value(MenuPhase, RaceCampaign, 5);
assert_enum_value(MenuPhase, NewSkirmishSingle, 6);
assert_enum_value(MenuPhase, NewSkirmishMulti, 6);
assert_enum_value(MenuPhase, NewSkirmishMultiLobby, 6);
assert_enum_value(MenuPhase, NewSkirmishGameSpy, 6);
assert_enum_value(MenuPhase, RaceSkirmish, 7);
assert_enum_value(MenuPhase, CustomCampaign, 8);
assert_enum_value(MenuPhase, Hotseat2NewSkirmishHotseat, 9);
assert_enum_value(MenuPhase, Hotseat2LoadSkirmishHotseat, 10);
assert_enum_value(MenuPhase, Multi2Session, 11);
assert_enum_value(MenuPhase, Session, 12);
assert_enum_value(MenuPhase, Unknown, 13);
assert_enum_value(MenuPhase, NewSkirmishHotseat2HotseatLobby, 14);
assert_enum_value(MenuPhase, NewSkirmish2LobbyHost, 15);
assert_enum_value(MenuPhase, Session2LobbyJoin, 15);
assert_enum_value(MenuPhase, LoadSkirmishMulti, 15);
assert_enum_value(MenuPhase, LoadSkirmishGameSpy, 15);
assert_enum_value(MenuPhase, Lord, 16);
assert_enum_value(MenuPhase, LoadCampaign, 16);
assert_enum_value(MenuPhase, LoadCustomCampaign, 16);
assert_enum_value(MenuPhase, LoadSkirmishSingle, 16);
assert_enum_value(MenuPhase, LoadSkirmishHotseat, 16);
assert_enum_value(MenuPhase, HotseatLobby, 16);
assert_enum_value(MenuPhase, LobbyHost, 16);
assert_enum_value(MenuPhase, LobbyJoin, 16);
assert_enum_value(MenuPhase, WaitInterf, 16);
assert_enum_value(MenuPhase, Main2Intro, 17);
assert_enum_value(MenuPhase, Main2Credits, 18);
assert_enum_value(MenuPhase, Credits, 19);
assert_enum_value(MenuPhase, Credits2Main, 20);
assert_enum_value(MenuPhase, Back2Main, 21);
assert_enum_value(MenuPhase, Main2Single, 22);
assert_enum_value(MenuPhase, Main2Proto, 23);
assert_enum_value(MenuPhase, Protocol2Hotseat, 24);
assert_enum_value(MenuPhase, Protocol2Multi, 25);
assert_enum_value(MenuPhase, Single2RaceCampaign, 26);
assert_enum_value(MenuPhase, Single2NewSkirmish, 27);
assert_enum_value(MenuPhase, NewSkirmish2RaceSkirmish, 28);
assert_enum_value(MenuPhase, CustomCampaign2RaceSkirmish, 28);
assert_enum_value(MenuPhase, Single2CustomCampaign, 29);
assert_enum_value(MenuPhase, NewSkirmishHotseat, 30);
assert_enum_value(MenuPhase, RaceCampaign2Lord, 31);
assert_enum_value(MenuPhase, Single2LoadCustomCampaign, 32);
assert_enum_value(MenuPhase, Single2LoadSkirmish, 33);
assert_enum_value(MenuPhase, Single2LoadCampaign, 34);
assert_enum_value(MenuPhase, Main2CustomLobby, 35);
assert_enum_value(MenuPhase, CustomLobby, 36);
assert_enum_value(MenuPhase, Back2CustomLobby, 37);
assert_enum_value(MenuPhase, NewSkirmishSingle2RandomScenarioSingle, 38);
assert_enum_value(MenuPhase, RandomScenarioSingle, 39);
assert_enum_value(MenuPhase, NewSkirmishHotseat2RandomScenarioHotseat, 40);
assert_enum_value(MenuPhase, RandomScenarioHotseat, 41);
assert_enum_value(MenuPhase, NewSkirmishMulti2RandomScenarioMulti, 42);
assert_enum_value(MenuPhase, RandomScenarioMulti, 43);

struct CMenuPhaseData
{
    CMidgard* midgard;
    CInterface* currentMenu;
    SmartPtr<CInterfManagerImpl> interfManager;
    MenuPhase currentPhase;
    ScenarioDataArrayWrapped* scenarios;
    SmartPtr<IMqImage2> transitionAnimation;
    int maxPlayers;
    bool networkGame;
    bool host;
    bool useGameSpy;
    char padding2;
    RaceCategoryList races;
    LRaceCategory race;
    LDifficultyLevel difficultyLevel;
    char* scenarioFilePath;
    CMidgardID scenarioFileId;
    CMidgardID campaignId;
    char* scenarioName;
    char* scenarioDescription;
    int suggestedLevel;
    bool unknown8;
    char padding[3];
    HANDLE scenarioFileHandle;
    int unknown10;
};

assert_size(CMenuPhaseData, 116);
assert_offset(CMenuPhaseData, races, 40);
assert_offset(CMenuPhaseData, scenarioFileId, 84);

struct CMenuPhase
    : public IMqNetSystem
    , public ICatalogValidate
{
    CMenuPhaseData* data;
};

assert_size(CMenuPhase, 12);
assert_offset(CMenuPhase, CMenuPhase::IMqNetSystem::vftable, 0);
assert_offset(CMenuPhase, CMenuPhase::ICatalogValidate::vftable, 4);

namespace CMenuPhaseApi {

struct Api
{
    using Constructor = CMenuPhase*(__thiscall*)(CMenuPhase* thisptr, int a2, int a3);
    Constructor constructor;

    /** Switches menu phase, implements menu screen transitions logic. */
    using SwitchPhase = void(__thiscall*)(CMenuPhase* thisptr, MenuTransition transition);
    SwitchPhase switchPhase;

    using CreateMenuCallback = CMenuBase*(__stdcall*)(CMenuPhase* menuPhase);

    /**
     * Replaces the currentMenu with a new menu created with the specified callback.
     * Optionally creates the specified animation (without actually playing it).
     * @param[in] menuPhase object to be passed to createMenuCallback.
     * @param[inout] currentPhase currently displayed phase, replaced with newPhase.
     * @param[in] interfManager manager to hide and show menu screens.
     * @param[inout] currentMenu currently displayed menu, replaced with the new menu.
     * @param[out] animation receives created animation, if animationName is provided.
     * @param[in] newPhase phase to be set as current.
     * @param[in] animationName name of animation to create, optional.
     * @param[in] createMenuCallback function that creates the new menu.
     */
    using ShowMenu = void(__stdcall*)(CMenuPhase* menuPhase,
                                      MenuPhase* currentPhase,
                                      SmartPtr<CInterfManagerImpl>* interfManager,
                                      CInterface** currentMenu,
                                      SmartPtr<IMqImage2>* animation,
                                      MenuPhase newPhase,
                                      const char* animationName,
                                      CreateMenuCallback** createMenuCallback);
    ShowMenu showMenu;

    /**
     * Replaces the currentMenu with CMenuFullScreenAnim that plays the specified animation.
     * @param[in] menuPhase object to be passed to CMenuFullScreenAnim constructor.
     * @param[inout] currentPhase currently displayed phase, replaced with newPhase.
     * @param[in] interfManager manager to hide and show menu screens.
     * @param[inout] currentMenu currently displayed menu, replaced with CMenuFullScreenAnim.
     * @param[in] newPhase phase to be set as current.
     * @param[in] animationName name of animation to show.
     */
    using ShowFullScreenAnimation = void(__stdcall*)(CMenuPhase* menuPhase,
                                                     MenuPhase* currentPhase,
                                                     SmartPtr<CInterfManagerImpl>* interfManager,
                                                     CInterface** currentMenu,
                                                     MenuPhase newPhase,
                                                     const char* animationName);
    ShowFullScreenAnimation showFullScreenAnimation;

    using SwitchToMenu = void(__thiscall*)(CMenuPhase* thisptr);
    using ShowTransition = void(__thiscall*)(CMenuPhase* thisptr, MenuTransition transition);

    // 21
    SwitchToMenu switchToMain;
    // 0
    ShowTransition transitionFromMain;
    // 22
    SwitchToMenu switchToSingle;
    // 1
    ShowTransition transitionFromSingle;
    // 2
    ShowTransition transitionFromProto;
    // 3
    ShowTransition transitionFromHotseat;
    // 26
    SwitchToMenu switchToRaceCampaign;
    // 5
    ShowTransition transitionGodToLord;
    // 29
    SwitchToMenu switchToCustomCampaign;
    // 8
    ShowTransition transitionNewQuestToGod;
    // 27
    SwitchToMenu switchToNewSkirmish;
    // 6
    SwitchToMenu transitionFromNewSkirmish;
    // 28
    SwitchToMenu switchToRaceSkirmish;
    // 7 reuses 5
    // 31
    SwitchToMenu switchToLord;
    // 33
    SwitchToMenu switchToLoadSkirmish;
    // 34
    SwitchToMenu switchToLoadCampaign;
    // 32
    SwitchToMenu switchToLoadCustomCampaign;
    // 17
    SwitchToMenu switchIntroToMain;
    // 18
    ShowTransition transitionToCredits;
    // 19 reuses 17
    // 23
    SwitchToMenu switchToProtocol;
    // 25
    SwitchToMenu switchToMulti;
    // 4
    ShowTransition transitionFromMulti;
    // 24
    SwitchToMenu switchToHotseat;
    // 9
    SwitchToMenu switchToNewSkirmishHotseat;
    // 10
    SwitchToMenu switchToLoadSkirmishHotseat;
    // 30
    SwitchToMenu transitionFromNewSkirmishHotseat;
    // 14
    SwitchToMenu switchToHotseatLobby;
    // 11
    SwitchToMenu switchToSession;
    // 15
    SwitchToMenu switchToLobbyHostJoin;
    // 16
    SwitchToMenu switchToWaitAndCreateClient;

    SwitchToMenu switchToGameSpy;

    SwitchToMenu switchToQuickLobby;

    using SwitchToMenuQuickLoad = void(__thiscall*)(CMenuPhase* thisptr, const char* saveFilePath);
    SwitchToMenuQuickLoad switchToQuickLoad;

    using ShowTransitionToMain = void(__thiscall*)(CMenuPhase* thisptr, bool showIntroTransition);
    ShowTransitionToMain transitionToMain;

    using ShowTransitionToMainOrCloseGame = void(__thiscall*)(CMenuPhase* thisptr,
                                                              bool showIntroTransition);
    ShowTransitionToMainOrCloseGame transitionToMainOrCloseGame;

    using SetString = void(__thiscall*)(CMenuPhase* thisptr, const char* string);
    using SetId = void(__thiscall*)(CMenuPhase* thisptr, const CMidgardID* scenarioFileId);

    SetId setCampaignId;
    SetId setScenarioFileId;
    SetString setScenarioFilePath;
    SetString setScenarioName;
    SetString setScenarioDescription;

    /** Closes the current scenario file and, if filePath is not nullptr, tries to open a new one.
     */
    using OpenScenarioFile = bool(__thiscall*)(CMenuPhase* thisptr, const char* filePath);
    OpenScenarioFile openScenarioFile;
};

Api& get();

IMqNetSystemVftable* vftable();

} // namespace CMenuPhaseApi

} // namespace game

#endif // MENUPHASE_H
