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

#ifndef MIDCOMMANDQUEUE2_H
#define MIDCOMMANDQUEUE2_H

#include "d2list.h"
#include "netmsgmapentry.h"
#include "smartptr.h"
#include "uievent.h"
#include <cstdint>

namespace game {

struct CMidgardID;
struct IMidScenarioObject;
struct CCommandMsg;
struct NetMsgEntryData;
struct NetMsgCallbacks;

/** Created and stored by CMidClientCore to process server messages (commands). */
struct CMidCommandQueue2
{
    struct INotifyCQVftable;
    struct IUpdateVftable;
    struct ICanIgnoreVftable;

    struct INotifyCQ
    {
        INotifyCQVftable* vftable;
    };

    struct IUpdate
    {
        IUpdateVftable* vftable;
    };

    struct ICanIgnore
    {
        ICanIgnoreVftable* vftable;
    };

    /** Net message map. */
    struct CNMMap
    {
        CMidCommandQueue2* commandQueue;
        NetMsgEntryData** netMsgEntryData;
    };

    struct CCommandMsgEntry
    {
        CCommandMsg* msg;
        /** commandUpdate methods are called for these objects (and their ids). */
        SmartPtr<IMidScenarioObject> objects;
    };

    IUpdate* commandUpdate;
    ICanIgnore* commandCanIgnore;
    CNMMap* netMessageMap;
    /** Command messages to process. */
    List<CCommandMsgEntry> commandsList;
    /** A current command is being processed. */
    bool processingCommand;
    /** commandUpdate was applied to a current command. */
    bool commandUpdateApplied;
    char padding[2];
    /**
     * Subscribers that are notified about new commands.
     * See CommandQueueMessageCallback
     */
    List<INotifyCQ*> notifyList;
    ListIterator<INotifyCQ*> currentNotify;
    /** Set to true when CPhaseGame is started and reset to false in CPhaseGame destuctor. */
    bool started;
    char padding2[3];
    /** MQ_COMMANDQUEUE2 window message id */
    unsigned int commandQueueMsgId;
    /** MQ_COMMANDQUEUE2 window message event */
    UiEvent commandQueueMsgEvent;
};

assert_size(CMidCommandQueue2, 92);
assert_size(CMidCommandQueue2::CNMMap, 8);

struct CMidCommandQueue2::INotifyCQVftable
{
    using Destructor = void(__thiscall*)(CMidCommandQueue2::INotifyCQ* thisptr, char flags);
    Destructor destructor;

    using Notify = void(__thiscall*)(CMidCommandQueue2::INotifyCQ* thisptr);
    Notify notify;
};

assert_vftable_size(CMidCommandQueue2::INotifyCQVftable, 2);

struct CMidCommandQueue2::IUpdateVftable
{
    using Destructor = void(__thiscall*)(CMidCommandQueue2::IUpdate* thisptr, char flags);
    Destructor destructor;

    using UpdateByObject = void(__thiscall*)(CMidCommandQueue2::IUpdate* thisptr,
                                             const IMidScenarioObject* object);
    UpdateByObject updateByObject;

    using UpdateByObjectId = bool(__thiscall*)(CMidCommandQueue2::IUpdate* thisptr,
                                               const CMidgardID* objectId);
    UpdateByObjectId updateByObjectId;
};

assert_vftable_size(CMidCommandQueue2::IUpdateVftable, 3);

struct CMidCommandQueue2::ICanIgnoreVftable
{
    using Destructor = void(__thiscall*)(CMidCommandQueue2::ICanIgnore* thisptr, char flags);
    Destructor destructor;

    using CanIgnore = bool(__thiscall*)(CMidCommandQueue2::ICanIgnore* thisptr,
                                        const CCommandMsg* commandMsg);
    CanIgnore canIgnore;
};

assert_vftable_size(CMidCommandQueue2::ICanIgnoreVftable, 2);

namespace CMidCommandQueue2Api {

struct Api
{
    /**
     * Moves currentNotify to the next entry of notifyList.
     * If currentNotify not reached the end, simply posts MQ_COMMANDQUEUE2 (commandQueueMsgId).
     * If it reaches the end then it means that all subscribers were notified about this command
     * message. Then ApplyCommandUpdate is called for this command, processingCommand is set to
     * false, the command is removed from commandsList and Update is called.
     */
    using ProcessCommands = void(__thiscall*)(CMidCommandQueue2* thisptr);
    ProcessCommands processCommands;

    using NMMapConstructor =
        CMidCommandQueue2::CNMMap*(__thiscall*)(CMidCommandQueue2::CNMMap* thisptr,
                                                NetMsgCallbacks** netCallbacks,
                                                CMidCommandQueue2* commandQueue);
    NMMapConstructor netMsgMapConstructor;

    /** Simply calls Push. */
    CNetMsgMapEntry_member::Callback netMsgMapQueueMessageCallback;

    /** Accesses the first message in the queue. Returns nullptr if there are no messages. */
    using Front = CCommandMsg*(__thiscall*)(CMidCommandQueue2* thisptr);
    Front front;

    /**
     * Checks lastCommandSequenceNumber, adds new entry to commandsList and runs Update.
     * Called only from CNetMsgMapEntry_member::Callback.
     */
    using Push = void(__thiscall*)(CMidCommandQueue2* thisptr, const CCommandMsg* commandMsg);
    Push push;

    /**
     * Gets the first command from commandsList. Then calls IUpdate::UpdateByObject and
     * IUpdate::UpdateByObjectId for every object in CCommandMsgEntry::objects.
     * If CCommandMsgEntry::objects is empty, the methods are still called once with null arguments.
     * Finally sets commandUpdateApplied to true.
     * Called from ProcessCommands and by IMidAnim2 instances (via CMidAnim2System), namely by:
     * CMidAnim2CityGrow, CMidAnim2LootRuin, CMidAnim2Mountain and CMidAnim2Spell.
     */
    using ApplyCommandUpdate = void(__thiscall*)(CMidCommandQueue2* thisptr);
    ApplyCommandUpdate applyCommandUpdate;

    /**
     * Checks if started is true, processingCommand is false and commandsList is not empty.
     * If so, calls StartProcessing.
     */
    using Update = void(__thiscall*)(CMidCommandQueue2* thisptr);
    Update update;

    /**
     * Sets processingCommand to true, commandUpdateApplied to false, and currentNotify to the
     * beginning of notifyList.
     * Then posts MQ_COMMANDQUEUE2 (commandQueueMsgId) via CUIManager.
     * Called only from Update.
     */
    using StartProcessing = void(__thiscall*)(CMidCommandQueue2* thisptr);
    StartProcessing startProcessing;

    /**
     * MQ_COMMANDQUEUE2 (commandQueueMsgId) window message callback.
     * If currentNotify is the first one, gets Front message and check if it can be ignored via
     * ICanIgnore::CanIgnore. If so, sets currentNotify to the end of notifyList and calls
     * ProcessCommands to effectively ignore the command. Otherwise simply calls Notify of the
     * currentNotify that starts command processing chain.
     */
    using CommandQueueMessageCallback = void(__thiscall*)(CMidCommandQueue2* thisptr,
                                                          unsigned int wParam,
                                                          long lParam);
    CommandQueueMessageCallback commandQueueMessageCallback;

    /**
     * Static counter, initialized to 0 and never ever being reset, that is a reason of some game
     * desync issues like leader "teleportation" (lack of movement animation).
     * If CCommandMsg::playerId is empty, this value is compared to CCommandMsg::sequenceNumber in
     * Push. If CCommandMsg::sequenceNumber is greater, the counter value is assigned to it and the
     * message is pushed to the queue, otherwise the message is rejected.
     * We know that this is unsigned because the associated assembly code uses jbe for comparison.
     */
    std::uint32_t* lastCommandSequenceNumber;
};

Api& get();

} // namespace CMidCommandQueue2Api

} // namespace game

#endif // MIDCOMMANDQUEUE2_H
