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

#include "cmdstackvisitmsg.h"
#include "d2string.h"
#include "dialoginterf.h"
#include "dynamiccast.h"
#include "gameimages.h"
#include "gameutils.h"
#include "isolayers.h"
#include "mainview2.h"
#include "mainview2hooks.h"
#include "mapgraphics.h"
#include "midclient.h"
#include "midclientcore.h"
#include "midcommandqueue2.h"
#include "midobjectlock.h"
#include "midsite.h"
#include "midtaskopeninterfparamresmarket.h"
#include "originalfunctions.h"
#include "phasegame.h"
#include "scenarioinfo.h"
#include "sitecategoryhooks.h"
#include "taskmanager.h"
#include "textboxinterf.h"
#include "togglebutton.h"

#include <spdlog/spdlog.h>
#include <textids.h>
#include <utils.h>

namespace hooks {

static bool gridVisible{false};

static void showGrid(int mapSize)
{
    using namespace game;

    const auto& imagesApi = GameImagesApi::get();

    GameImagesPtr imagesPtr;
    imagesApi.getGameImages(&imagesPtr);
    auto images = *imagesPtr.data;

    const auto& mapGraphics{MapGraphicsApi::get()};

    for (int x = 0; x < mapSize; ++x) {
        for (int y = 0; y < mapSize; ++y) {
            auto gridImage{imagesApi.getImage(images->isoCmon, "GRID", 0, true, images->log)};
            if (!gridImage) {
                continue;
            }

            const CMqPoint mapPosition{x, y};
            mapGraphics.showImageOnMap(&mapPosition, isoLayers().grid, gridImage, 0, 0);
        }
    }

    imagesApi.createOrFreeGameImages(&imagesPtr, nullptr);
}

static void hideGrid()
{
    using namespace game;

    MapGraphicsApi::get().hideLayerImages(isoLayers().grid);
}

static void __fastcall mainView2OnToggleGrid(game::CMainView2* thisptr,
                                             int /*%edx*/,
                                             bool toggleOn,
                                             game::CToggleButton*)
{
    gridVisible = toggleOn;

    if (gridVisible) {
        auto objectMap{game::CPhaseApi::get().getDataCache(&thisptr->phaseGame->phase)};
        auto scenarioInfo{getScenarioInfo(objectMap)};

        showGrid(scenarioInfo->mapSize);
        return;
    }

    hideGrid();
}

void __fastcall mainView2ShowIsoDialogHooked(game::CMainView2* thisptr, int /*%edx*/)
{
    using namespace game;

    const auto& mainViewApi{CMainView2Api::get()};

    mainViewApi.showDialog(thisptr, nullptr);

    static const char buttonName[]{"TOG_GRID"};

    const auto& dialogApi{CDialogInterfApi::get()};
    auto dialog{thisptr->dialogInterf};

    if (!dialogApi.findControl(dialog, buttonName)) {
        // Grid button was not added to Interf.dlg, skip
        return;
    }

    auto toggleButton{dialogApi.findToggleButton(dialog, buttonName)};
    if (!toggleButton) {
        // Control was found, but it is not CToggleButton
        spdlog::error("{:s} in {:s} must be a toggle button", buttonName, dialog->data->dialogName);
        return;
    }

    using ButtonCallback = CMainView2Api::Api::ToggleButtonCallback;

    ButtonCallback callback{};
    callback.callback = (ButtonCallback::Callback)&mainView2OnToggleGrid;

    SmartPointer functor;
    mainViewApi.createToggleButtonFunctor(&functor, 0, thisptr, &callback);

    const auto& buttonApi{CToggleButtonApi::get()};
    buttonApi.assignFunctor(dialog, buttonName, dialog->data->dialogName, &functor, 0);
    SmartPointerApi::get().createOrFreeNoDtor(&functor, nullptr);

    buttonApi.setChecked(toggleButton, gridVisible);

    static const char turnTextName[]{"TXT_TURN"};
    const auto& textApi = CTextBoxInterfApi::get();

    auto textBox = dialogApi.findTextBox(dialog, turnTextName);
    if (textBox && textBox->data) {
        auto objectMap = CPhaseApi::get().getDataCache(&thisptr->phaseGame->phase);
        auto scenarioInfo = getScenarioInfo(objectMap);

        if (scenarioInfo) {
            // Extract the current text from the DLG
            std::string text = textBox->data->text.string ? textBox->data->text.string : "";
            spdlog::debug("Current turn text before update: '{}'", text);

            // If the text is empty or does not contain the %TURN% placeholder
            if (text.empty() || text.find("%TURN%") == std::string::npos) {
                // Try to load the text ID from textids.lua
                std::string textId = hooks::textIds().interf.currentTurn;
                if (!textId.empty()) {
                    auto idText = getInterfaceText(textId.c_str());
                    if (!idText.empty()) {
                        text = idText;
                        spdlog::debug(
                            "TXT_TURN fallback loaded from textIds().interf.currentTurn = '{}'",
                            textId);
                    }
                }

                // If both the DLG and Lua values are missing, use the default template
                if (text.empty()) {
                    spdlog::debug("TXT_TURN missing or no placeholder — using default template");
                    text = "Current turn %TURN%";
                }
            }

            // Replace the %TURN% placeholder with the current turn number
            replace(text, "%TURN%", fmt::format("{}", scenarioInfo->currentTurn));

            // Set the text back to the UI element
            textApi.setString(textBox, text.c_str());
            spdlog::debug("TXT_TURN updated to: '{}'", text);
        }
    } else {
        spdlog::warn("TXT_TURN not found in dialog");
    }
}

void __fastcall mainView2HandleCmdStackVisitMsgHooked(game::CMainView2* thisptr,
                                                      int /*%edx*/,
                                                      const game::CCommandMsg* stackVisitMsg)
{
    using namespace game;

    const auto& dynamicCast{RttiApi::get().dynamicCast};
    const auto& rtti{RttiApi::rtti()};

    auto msg{(const CCmdStackVisitMsg*)dynamicCast(stackVisitMsg, 0, rtti.CCommandMsgType,
                                                   rtti.CCmdStackVisitMsgType, 0)};

    const auto& phaseApi{CPhaseApi::get()};
    CPhase* phase{&thisptr->phaseGame->phase};

    IMidgardObjectMap* objectMap{phaseApi.getDataCache(phase)};

    auto obj{objectMap->vftable->findScenarioObjectById(objectMap, &msg->siteId)};
    auto site{
        (const CMidSite*)dynamicCast(obj, 0, rtti.IMidScenarioObjectType, rtti.CMidSiteType, 0)};
    if (!customSiteCategories().exists
        || customSiteCategories().resourceMarket.id != site->siteCategory.id) {
        return getOriginalFunctions().handleCmdStackVisitMsg(thisptr, stackVisitMsg);
    }

    // Handle stack visiting resource market
    ITaskManagerHolder* holder{&thisptr->taskManagerHolder};
    CTaskManager* taskManager{holder->vftable->getTaskManager(holder)};

    const CMidgardID& siteId{msg->siteId};
    const CMidgardID& visitorStackId{msg->visitorStackId};

    ITask* task{createMidTaskOpenInterfParamResMarket(taskManager, thisptr->phaseGame,
                                                      visitorStackId, siteId)};

    auto commandQueue{phaseApi.getCommandQueue(phase)};
    CMidCommandQueue2Api::get().processCommands(commandQueue);

    CTaskManagerApi::get().setCurrentTask(taskManager, task);
}

void __fastcall mainView2CommandQueueCallbackHooked(game::CMainView2* thisptr, int /*%edx*/)
{
    using namespace game;

    const auto& phaseApi = CPhaseApi::get();
    const auto& commandQueueApi = CMidCommandQueue2Api::get();

    auto commandQueue = phaseApi.getCommandQueue(&thisptr->phaseGame->phase);
    auto message = commandQueueApi.front(commandQueue);
    if (message) {
        auto messageId = message->vftable->getId(message);
        if (messageId == CommandMsgId::MoveStackEnd) {
            thisptr->phaseGame->data->midObjectLock->patched.movingStack = false;
            spdlog::debug(__FUNCTION__ ": CMidObjectLock::movingStack set to false");
            commandQueueApi.processCommands(commandQueue);
            return;
        }
    }

    return getOriginalFunctions().mainView2CommandQueueCallback(thisptr);
}

} // namespace hooks
