/*
 * This file is part of the modding toolset for Disciples 2.
 * (https://github.com/VladimirMakeev/D2ModdingToolset)
 * Copyright (C) 2023 Vladimir Makeev.
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

#include "menurandomscenariomulti.h"
#include "dialoginterf.h"
#include "editboxinterf.h"
#include "gamesettings.h"
#include "mempool.h"
#include "menunewskirmishmulti.h"
#include "menuphase.h"
#include "midgard.h"
#include "restrictions.h"

namespace hooks {

static constexpr const char dialogName[] = "DLG_RANDOM_SCENARIO_MULTI";

static void startScenarioNet(CMenuRandomScenario* menu)
{
    prepareToStartRandomScenario(menu, true);

    using namespace game;

    // Setup game host: reuse original game logic that creates player server and client
    if (CMenuNewSkirmishMultiApi::get().createServer(menu)) {
        CMenuPhaseApi::get().switchPhase(menu->menuBaseData->menuPhase,
                                         MenuTransition::RandomScenarioMulti2LobbyHost);
    }
}

CMenuRandomScenarioMulti::CMenuRandomScenarioMulti(game::CMenuPhase* menuPhase)
    : CMenuRandomScenario(menuPhase, startScenarioNet, dialogName)
{
    using namespace game;

    const auto& menuBase{CMenuBaseApi::get()};
    const auto& dialogApi{CDialogInterfApi::get()};
    const auto& editBoxApi{CEditBoxInterfApi::get()};
    const auto& restrictions{gameRestrictions()};

    CDialogInterf* dialog{menuBase.getDialogInterface(this)};

    CEditBoxInterf* editName{editBoxApi.setFilterAndLength(dialog, "EDIT_NAME", dialogName,
                                                           EditFilter::Names,
                                                           *restrictions.playerNameMaxLength)};
    if (editName) {
        const CMidgard* midgard{CMidgardApi::get().instance()};
        const GameSettings* settings{*midgard->data->settings};

        editBoxApi.setString(editName, settings->defaultPlayerName.string);
    }

    editBoxApi.setFilterAndLength(dialog, "EDIT_GAME", dialogName, EditFilter::Names,
                                  restrictions.gameNameMaxLength);
    editBoxApi.setFilterAndLength(dialog, "EDIT_PASSWORD", dialogName, EditFilter::Names,
                                  restrictions.passwordMaxLength);
}

game::CMenuBase* __stdcall createMenuRandomScenarioMulti(game::CMenuPhase* menuPhase)
{
    const auto& allocateMem{game::Memory::get().allocate};

    auto menu = (CMenuRandomScenarioMulti*)allocateMem(sizeof(CMenuRandomScenarioMulti));
    new (menu) CMenuRandomScenarioMulti(menuPhase);

    return menu;
}

} // namespace hooks
