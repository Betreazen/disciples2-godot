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

#include "menucustomnewskirmishmulti.h"
#include "button.h"
#include "dialoginterf.h"
#include "editboxinterf.h"
#include "game.h"
#include "interfaceutils.h"
#include "listbox.h"
#include "mempool.h"
#include "menuphase.h"
#include "scenariodata.h"
#include "scenariodataarray.h"
#include "textids.h"
#include "utils.h"
#include <spdlog/spdlog.h>

namespace hooks {

CMenuCustomNewSkirmishMulti::CMenuCustomNewSkirmishMulti(game::CMenuPhase* menuPhase)
    : CMenuCustomBase{this}
    , m_roomsCallback{this}
{
    using namespace game;

    CMenuNewSkirmishMultiApi::get().constructor(this, menuPhase);

    static RttiInfo<CMenuNewSkirmishMultiVftable> rttiInfo = {};
    if (rttiInfo.locator == nullptr) {
        replaceRttiInfo(rttiInfo, (CMenuNewSkirmishMultiVftable*)this->vftable);
        rttiInfo.vftable.destructor = (CInterfaceVftable::Destructor)&destructor;
    }
    this->vftable = &rttiInfo.vftable;

    auto dialog = CMenuBaseApi::get().getDialogInterface(this);
    setButtonCallback(dialog, "BTN_LOAD", loadBtnHandler, this);
    setUserNameToEditName();

    CNetCustomService::get()->addRoomsCallback(&m_roomsCallback);
}

CMenuCustomNewSkirmishMulti::~CMenuCustomNewSkirmishMulti()
{
    using namespace game;

    auto service = CNetCustomService::get();
    if (service) {
        service->removeRoomsCallback(&m_roomsCallback);
    }

    CMenuNewSkirmishMultiApi::get().destructor(this);
}

void __fastcall CMenuCustomNewSkirmishMulti::destructor(CMenuCustomNewSkirmishMulti* thisptr,
                                                        int /*%edx*/,
                                                        char flags)
{
    thisptr->~CMenuCustomNewSkirmishMulti();

    if (flags & 1) {
        spdlog::debug("Free CMenuCustomNewSkirmishMulti memory");
        game::Memory::get().freeNonZero(thisptr);
    }
}

void __fastcall CMenuCustomNewSkirmishMulti::loadBtnHandler(CMenuCustomNewSkirmishMulti* thisptr,
                                                            int /*%edx*/)
{
    using namespace game;

    const auto& fn = gameFunctions();
    const auto& idApi = CMidgardIDApi::get();
    const auto& menuPhaseApi = CMenuPhaseApi::get();
    const auto& raceCategoryListApi = RaceCategoryListApi::get();

    auto scenario = thisptr->getSelectedScenario();
    if (!scenario) {
        return;
    }

    if (!thisptr->isGameAndPlayerNamesValid()) {
        // Please enter valid game and player names
        showMessageBox(getInterfaceText("X005TA0867"));
        return;
    }

    CMenuPhase* menuPhase = thisptr->menuBaseData->menuPhase;
    CMenuPhaseData* data = menuPhase->data;
    auto races = &data->races;
    raceCategoryListApi.freeNodes(races);
    for (const auto& race : scenario->races) {
        if (!fn.isRaceCategoryUnplayable(&race)) {
            raceCategoryListApi.add(races, &race);
        }
    }
    if (!races->length) {
        spdlog::debug("A scenario selected for a new skirmish has no playable races");
        return;
    }

    data->unknown8 = true;
    data->suggestedLevel = scenario->suggestedLevel;
    data->maxPlayers = races->length;

    // Set special (skirmish) campaign id
    CMidgardID campaignId;
    menuPhaseApi.setCampaignId(menuPhase, idApi.fromString(&campaignId, "C000CC0001"));
    if (scenario->filePath.length) {
        menuPhaseApi.setScenarioFilePath(menuPhase, scenario->filePath.string);
    } else {
        menuPhaseApi.setScenarioFileId(menuPhase, &scenario->scenarioFileId);
    }
    menuPhaseApi.setScenarioName(menuPhase, scenario->name.string);
    menuPhaseApi.setScenarioDescription(menuPhase, scenario->description.string);

    auto dialog = CMenuBaseApi::get().getDialogInterface(thisptr);
    thisptr->createRoom(getEditBoxText(dialog, "EDIT_GAME"), scenario->name.string,
                        scenario->description.string, getEditBoxText(dialog, "EDIT_PASSWORD"));
}

const game::ScenarioData* CMenuCustomNewSkirmishMulti::getSelectedScenario()
{
    using namespace game;

    auto dialog = CMenuBaseApi::get().getDialogInterface(this);
    auto listBox = CDialogInterfApi::get().findListBox(dialog, "TLBOX_GAME_SLOT");
    auto selectedIndex = listBox->listBoxData->selectedElement;

    CMenuPhase* menuPhase = menuBaseData->menuPhase;
    const ScenarioDataArray* scenarios = &menuPhase->data->scenarios->data;

    return (0 <= selectedIndex && selectedIndex < (int)scenarios->size())
               ? &scenarios->bgn[selectedIndex]
               : nullptr;
}

bool CMenuCustomNewSkirmishMulti::isGameAndPlayerNamesValid()
{
    using namespace game;

    auto dialog = CMenuBaseApi::get().getDialogInterface(this);
    auto gameName = getEditBoxText(dialog, "EDIT_GAME");
    if (!gameName || !strlen(gameName)) {
        return false;
    }

    auto playerName = getEditBoxText(dialog, "EDIT_NAME");
    if (!playerName || !strlen(playerName)) {
        return false;
    }

    return true;
}

void CMenuCustomNewSkirmishMulti::RoomsCallback::CreateRoom_Callback(
    const SLNet::SystemAddress& senderAddress,
    SLNet::CreateRoom_Func* callResult)
{
    using namespace game;

    m_menu->hideWaitDialog();

    switch (auto resultCode = callResult->resultCode) {
    case SLNet::REC_SUCCESS: {
        if (!CMenuNewSkirmishMultiApi::get().createServer(m_menu)) {
            // Should not happen at any circumstances
            spdlog::debug("Unable to create server for a new skirmish");
            CNetCustomService::get()->leaveRoom();
            break;
        }

        CMenuPhase* menuPhase = m_menu->menuBaseData->menuPhase;
        CMenuPhaseApi::get().switchPhase(menuPhase, MenuTransition::NewSkirmishMulti2LobbyHost);
        break;
    }

    default: {
        auto msg{getInterfaceText(textIds().lobby.createRoomFailed.c_str())};
        if (msg.empty()) {
            msg = "Could not create a room.\n%ERROR%";
        }
        replace(msg, "%ERROR%", SLNet::RoomsErrorCodeDescription::ToEnglish(resultCode));
        showMessageBox(msg);
        break;
    }
    }
}

} // namespace hooks
