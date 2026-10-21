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

#include "netcustompeer.h"
#include "uimanager.h"
#include <spdlog/spdlog.h>

namespace hooks {

CNetCustomPeer::CNetCustomPeer(const char* packetNotificationMessageName)
    : m_packetNotificationSent(false)
{
    using namespace game;

    const auto& uiManagerApi = CUIManagerApi::get();

    spdlog::debug(__FUNCTION__ ": messageName = {:s}", packetNotificationMessageName);

    SetUserUpdateThread(UpdateThreadCallback, nullptr);

    UIManagerPtr uiManager{};
    uiManagerApi.get(&uiManager);
    m_packetNotificationMessageId = uiManagerApi.registerMessage(uiManager.data,
                                                                 packetNotificationMessageName);
    SmartPointerApi::get().createOrFree((SmartPointer*)&uiManager, nullptr);
}

CNetCustomPeer::~CNetCustomPeer()
{
    spdlog::debug(__FUNCTION__);
}

bool CNetCustomPeer::IsPacketNotificationSent() const
{
    return m_packetNotificationSent;
}

void CNetCustomPeer::ResetPacketNotification()
{
    m_packetNotificationSent = false;
}

void CNetCustomPeer::UpdateThreadCallback(RakPeerInterface* peer, void* /*data*/)
{
    // TODO: remove this when disconnect issue is resolved.
    // Otherwise we want to know if the network thread is responsive (once every 5 secs is enough)
    static DWORD lastUpdate = 0;
    DWORD tickCount = GetTickCount();
    if (DWORD(tickCount - lastUpdate) > 5000) {
        spdlog::debug(__FUNCTION__ ": the network thread appears responsive");
        lastUpdate = tickCount;
    }

    CNetCustomPeer* customPeer = (CNetCustomPeer*)peer;
    customPeer->packetReturnMutex.Lock();
    bool empty = customPeer->packetReturnQueue.IsEmpty();
    customPeer->packetReturnMutex.Unlock();

    if (!empty && !customPeer->IsPacketNotificationSent()) {
        spdlog::debug(
            __FUNCTION__ ": there are packets in the return queue, posting notification message");
        customPeer->SendPacketNotification();
    }
}

void CNetCustomPeer::SendPacketNotification()
{
    using namespace game;

    const auto& uiManagerApi = CUIManagerApi::get();

    UIManagerPtr uiManager{};
    uiManagerApi.get(&uiManager);
    m_packetNotificationSent = uiManagerApi.postMessage(uiManager.data,
                                                        m_packetNotificationMessageId, 0, 0);
    if (!m_packetNotificationSent) {
        spdlog::debug(__FUNCTION__ ": failed to post notification message, error = {:d}",
                      GetLastError());
    }
    SmartPointerApi::get().createOrFree((SmartPointer*)&uiManager, nullptr);
}

} // namespace hooks
