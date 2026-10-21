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

#include "menunewskirmishmultihooks.h"
#include "button.h"
#include "dialoginterf.h"
#include "netcustomplayerserver.h"
#include "netcustomservice.h"
#include "netcustomsession.h"
#include "originalfunctions.h"
#include <spdlog/spdlog.h>

namespace hooks {

static void __fastcall showMenuRandomScenarioMulti(game::CMenuNewSkirmishMulti* thisptr,
                                                   int /*%edx*/)
{
    using namespace game;

    // Transfer to a new random scenario generation menu, from state 6 to 41
    CMenuPhase* menuPhase{thisptr->menuBaseData->menuPhase};
    CMenuPhaseApi::get().switchPhase(menuPhase,
                                     MenuTransition::NewSkirmishMulti2RandomScenarioMulti);
}

game::CMenuNewSkirmishMulti* __fastcall menuNewSkirmishMultiCtorHooked(
    game::CMenuNewSkirmishMulti* thisptr,
    int /*%edx*/,
    game::CMenuPhase* menuPhase)
{
    getOriginalFunctions().menuNewSkirmishMultiCtor(thisptr, menuPhase);

    using namespace game;

    static const char buttonName[] = "BTN_RANDOM_MAP";
    const auto& menuBase{CMenuBaseApi::get()};
    const auto& dialogApi{CDialogInterfApi::get()};

    CDialogInterf* dialog{menuBase.getDialogInterface(thisptr)};

    // Check if we have new button for random scenario generator menu and setup its callback
    if (dialogApi.findControl(dialog, buttonName)) {
        const auto& button{CButtonInterfApi::get()};

        SmartPointer functor;
        auto callback = (CMenuBaseApi::Api::ButtonCallback)showMenuRandomScenarioMulti;

        menuBase.createButtonFunctor(&functor, 0, thisptr, &callback);
        button.assignFunctor(dialog, buttonName, "DLG_HOST", &functor, 0);
        SmartPointerApi::get().createOrFreeNoDtor(&functor, nullptr);
    }

    return thisptr;
}

} // namespace hooks
