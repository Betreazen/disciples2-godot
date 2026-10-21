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

#ifndef NETCUSTOMSERVICE_H
#define NETCUSTOMSERVICE_H

#include "mqnetservice.h"
#include "netmsg.h"
#include "uievent.h"
#include <Lobby2Client.h>
#include <Lobby2Message.h>
#include <MessageIdentifiers.h>
#include <PacketPriority.h>
#include <RakPeerInterface.h>
#include <RoomsPlugin.h>
#include <filesystem>
#include <mutex>
#include <string>
#include <vector>

namespace game {
struct NetMessageHeader;
} // namespace game

namespace hooks {

// !!! Keep in sync with lobby server
// Should not exceed the size of SLNet::MessageID
enum ClientMessages
{
    ID_LOBBY_CHAT_MESSAGE = ID_USER_PACKET_ENUM + 1,
    ID_LOBBY_GET_ONLINE_USERS_REQUEST,
    ID_LOBBY_GET_ONLINE_USERS_RESPONSE,
    ID_LOBBY_GET_CHAT_MESSAGES_REQUEST,
    ID_LOBBY_GET_CHAT_MESSAGES_RESPONSE,
    ID_GAME_MESSAGE_TO_HOST_SERVER,
    ID_GAME_MESSAGE_TO_HOST_CLIENT,
    ID_GAME_MESSAGE = game::netMessageNormalType & 0xff,
};

class CNetCustomPeer;
class CNetCustomSession;

class NetPeerCallback
{
public:
    virtual ~NetPeerCallback() = default;
    virtual void onPacketReceived(DefaultMessageIDTypes type,
                                  SLNet::RakPeerInterface* peer,
                                  const SLNet::Packet* packet) = 0;
};

// Used in CNetCustomService::joinSession instead of IMqNetSessEnum
struct CNetCustomSessEnum
{
    SLNet::RakNetGUID serverGuid;
    std::string sessionName;
};

class CNetCustomService : public game::IMqNetService
{
public:
    struct UserInfo
    {
        SLNet::RakNetGUID guid;
        SLNet::RakString name;
    };

    struct ChatMessage
    {
        SLNet::RakString sender;
        SLNet::RakString text;
    };

    // !!! Keep in sync with lobby server
    static constexpr std::uint32_t peerConnectionTimeout{30000};
    static constexpr std::uint32_t peerShutdownTimeout{100};
    // static constexpr std::uint32_t peerProcessInterval{10};
    static constexpr char peerProcessMessageName[] = "MIDGARD CUSTOM LOBBY NETMSG";
    static constexpr char titleName[] = "Disciples II: Rise of the Elves";
    static constexpr char titleSecretKey[] = "TheVerySecretKey";
    static constexpr char gameFilesHashColumnName[] = "FilesHash";
    static constexpr char gameVersionColumnName[] = "GameVersion";
    static constexpr char gameNameColumnName[] = "GameName";
    static constexpr char passwordColumnName[] = "Password";
    static constexpr char scenarioNameColumnName[] = "ScenarioName";
    static constexpr char scenarioDescriptionColumnName[] = "ScenarioDescription";
    // See SLNet::Lobby2Message::ValidatePassword
    static constexpr std::uint32_t passwordMaxLength{50};

    /** Returns the service instance if it is set in CMidgard, otherwise returns nullptr. */
    static CNetCustomService* get();

    CNetCustomService();
    ~CNetCustomService();

    bool connect();
    CNetCustomSession* getSession() const;
    const std::string& getUserName() const;
    bool connected() const;
    bool loggedIn() const;
    const SLNet::RakNetGUID getPeerGuid() const;
    const SLNet::RakNetGUID getLobbyGuid() const;
    bool send(const SLNet::BitStream& stream,
              const SLNet::RakNetGUID& to,
              PacketPriority priority) const;
    const std::string& getGameFilesHash();
    std::vector<std::filesystem::path> getGameFilesToHash() const;
    UserInfo getUserInfo() const;
    void processPeerMessages() const;

    bool registerAccount(const char* userName, const char* password);
    bool login(const char* userName, const char* password);
    void logoff();

    void sendChatMessage(const char* text);
    ChatMessage readChatMessage(const SLNet::Packet* packet);

    /** Requests online user list. Handle ID_LOBBY_GET_ONLINE_USERS_RESPONSE in peer callback. */
    void queryOnlineUsers();
    std::vector<UserInfo> readOnlineUsers(const SLNet::Packet* packet);

    /** Requests saved chat messages. Handle ID_LOBBY_GET_CHAT_MESSAGES_RESPONSE in peer callback.
     */
    void queryChatMessages();
    std::vector<ChatMessage> readChatMessages(const SLNet::Packet* packet);

    /** Tries to create and enter a new room. */
    bool createRoom(const char* gameName,
                    const char* scenarioName,
                    const char* scenarioDescription,
                    const char* password = nullptr);

    /** Requests to leave the previously entered room. */
    void leaveRoom();

    /** Requests a list of rooms. */
    void searchRooms();

    /** Requests joining a room. */
    void joinRoom(SLNet::RoomID id);

    /** Tries to change number of public slots in current room. */
    bool changeRoomPublicSlots(unsigned int publicSlots);

    /**
     * The service is always first to receive peer notifications.
     * So other listeners will be dealing with already updated service state.
     */
    void addPeerCallback(NetPeerCallback* callback);
    void removePeerCallback(NetPeerCallback* callback);

    /**
     * The service is always first to receive lobby notifications.
     * So other listeners will be dealing with already updated service state.
     */
    void addLobbyCallback(SLNet::Lobby2Callbacks* callback);
    void removeLobbyCallback(SLNet::Lobby2Callbacks* callback);

    /**
     * The service is always first to receive room notifications.
     * So other listeners will be dealing with already updated service state.
     */
    void addRoomsCallback(SLNet::RoomsCallback* callback);
    void removeRoomsCallback(SLNet::RoomsCallback* callback);

protected:
    bool startPeer();

    // IMqNetService
    static void __fastcall destructor(CNetCustomService* thisptr, int /*%edx*/, char flags);
    static bool __fastcall hasSessions(CNetCustomService* thisptr, int /*%edx*/);
    static void __fastcall getSessions(CNetCustomService* thisptr,
                                       int /*%edx*/,
                                       game::List<game::IMqNetSessEnum*>* sessions,
                                       const GUID* appGuid,
                                       const char* ipAddress,
                                       bool allSessions,
                                       bool requirePassword);
    static void __fastcall createSession(CNetCustomService* thisptr,
                                         int /*%edx*/,
                                         game::IMqNetSession** netSession,
                                         const GUID* /* appGuid */,
                                         const char* sessionName,
                                         const char* password);
    static void __fastcall joinSession(CNetCustomService* thisptr,
                                       int /*%edx*/,
                                       game::IMqNetSession** netSession,
                                       CNetCustomSessEnum* netSessionEnum,
                                       const char* password);

private:
    static game::IMqNetServiceVftable g_vftable;

    class PeerCallback : public NetPeerCallback
    {
    public:
        PeerCallback(CNetCustomService* service)
            : m_service(service)
        { }

        void onPacketReceived(DefaultMessageIDTypes type,
                              SLNet::RakPeerInterface* peer,
                              const SLNet::Packet* packet);

    private:
        CNetCustomService* m_service;
    };

    class LobbyCallback : public SLNet::Lobby2Callbacks
    {
    public:
        LobbyCallback(CNetCustomService* service)
            : m_service(service)
        { }

        ~LobbyCallback() override = default;

        void MessageResult(SLNet::Client_Login* message) override;
        void MessageResult(SLNet::Client_Logoff* message) override;
        void MessageResult(SLNet::Notification_Client_RemoteLogin* message) override;
        void ExecuteDefaultResult(SLNet::Lobby2Message* msg) override;

    private:
        CNetCustomService* m_service;
    };

    class RoomsCallback : public SLNet::RoomsCallback
    {
    public:
        RoomsCallback() = default;
        ~RoomsCallback() override = default;

        void CreateRoom_Callback(const SLNet::SystemAddress& senderAddress,
                                 SLNet::CreateRoom_Func* callResult) override;

        void EnterRoom_Callback(const SLNet::SystemAddress& senderAddress,
                                SLNet::EnterRoom_Func* callResult) override;

        void LeaveRoom_Callback(const SLNet::SystemAddress& senderAddress,
                                SLNet::LeaveRoom_Func* callResult) override;

        void RoomMemberLeftRoom_Callback(
            const SLNet::SystemAddress& senderAddress,
            SLNet::RoomMemberLeftRoom_Notification* notification) override;

        void RoomMemberJoinedRoom_Callback(
            const SLNet::SystemAddress& senderAddress,
            SLNet::RoomMemberJoinedRoom_Notification* notification) override;

    protected:
        void ExecuteDefaultResult(const char* callbackName,
                                  SLNet::RoomsErrorCode resultCode,
                                  SLNet::RoomID roomId,
                                  SLNet::RoomDescriptor* roomDescriptor = nullptr) const;
    };

    static void __fastcall peerProcessEventCallback(const CNetCustomService* thisptr,
                                                    int /*%edx*/,
                                                    unsigned int,
                                                    long);
    std::vector<NetPeerCallback*> getPeerCallbacks() const;

    bool m_connected;
    PeerCallback m_peerCallback;
    CNetCustomSession* m_session;
    std::string m_userName;
    /** Interacts with lobby server. */
    SLNet::Lobby2Client m_lobbyClient;
    /** Creates network messages. */
    SLNet::Lobby2MessageFactory m_lobbyMsgFactory;
    LobbyCallback m_lobbyCallback;
    /** Interacts with lobby server rooms. */
    SLNet::RoomsPlugin m_roomsClient;
    RoomsCallback m_roomsCallback;
    /** Connection with lobby server. */
    CNetCustomPeer* m_peer;
    game::UiEvent m_peerProcessEvent;
    std::vector<NetPeerCallback*> m_peerCallbacks;
    mutable std::mutex m_peerCallbacksMutex;
    std::string m_gameFilesHash;
};

assert_offset(CNetCustomService, vftable, 0);

} // namespace hooks

#endif // NETCUSTOMSERVICE_H
