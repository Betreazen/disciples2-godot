/*
 * This file is part of the modding toolset for Disciples 2.
 * (https://github.com/VladimirMakeev/D2ModdingToolset)
 * Copyright (C) 2026 Disciples-Godot probe.
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

#include "probe.h"
#include "gameutils.h"
#include "midgardid.h"
#include "midgardobjectmap.h"
#include "midplayer.h"
#include "midstack.h"
#include <cstdint>
#include <fmt/format.h>
#include <string>
#include <vector>

/*
 * Схема корпуса 6.0, партия 1: воры и дипломатия глазами эффектов карты - действие вора с тем,
 * кто действовал, отмена, список доступных действий на подходе, предложение и смена союза, обмен.
 *
 * Эффекты складываются серверным разбором сообщения в список и сразу применяются, ИИ ходит теми
 * же сообщениями - поэтому строки общие для человека и ИИ. Адреса - Gog 3.01, разбор -
 * disciples-modding/.superpowers/sdd/2026-09-14-probe-v6/spec-thieves-diplo.md; слоты vftable и
 * ширина ret сверены capstone. Поля эффектов снимаются до оригинала через readAt.
 */

namespace probe {

namespace {

using namespace game;

/** Absent or unreadable values are journaled as this, never as a plausible number. */
constexpr int notComputed = -1;

/** Потолок числа действий вора в строке: в ванили 14 категорий. */
constexpr int maxNobleActions = 32;

/** Потолок шагов обхода дерева множества - против битого указателя, замыкающего цикл. */
constexpr int maxTreeSteps = 256;

using EffectApply = bool(__fastcall*)(const void* thisptr,
                                      int edx,
                                      IMidgardObjectMap* objectMap,
                                      void* sender,
                                      void* triggerers);

std::string idOrNull(bool readableValue, const CMidgardID& id)
{
    return readableValue ? idToString(&id) : std::string{"null"};
}

/** Владелец отряда или "null" (CMidStack::ownerId - то же поле берёт движок через 0x5f501a). */
std::string ownerOf(const IMidgardObjectMap* objectMap, bool ok, const CMidgardID* stackId)
{
    const auto* stack = ok ? readableStack(objectMap, stackId) : nullptr;
    return stack ? idToString(&stack->ownerId) : std::string{"null"};
}

/** CMidPlayer::isHuman (+0x88) как JSON: true/false, null - игрока не прочитать. */
std::string humanOf(const IMidgardObjectMap* objectMap, bool ok, const CMidgardID* playerId)
{
    const auto* player = ok ? readablePlayer(objectMap, playerId) : nullptr;
    if (!player)
        return {"null"};

    return player->isHuman ? "true" : "false";
}

/* noble_action - CEffectNobleAction::apply 0x43a335 */

/** Раскладка по конструктору 0x43a29c. */
struct NobleActionEffect
{
    const void* vftable;
    CMidgardID stackId;          // +4, действовавший вор
    CMidgardID targetId;         // +8
    CMidgardID resultId;         // +0xc, INobleActionResult::getId
    int value;                   // +0x10, INobleActionResult::getValue
    const void* categoryVftable; // +0x14, LNobleActionCat
    const void* categoryTable;   // +0x18
    int categoryId;              // +0x1c
    std::uint8_t success;        // +0x20
};

EffectApply origNobleActionApply{};

/**
 * Действие вора - и успех, и провал: объект строится до проверки броска (0x443135) и кладётся в
 * список первым, эффекты результата - позже. Строка лежит сразу за thief_roll того же действия.
 * Внутри при успехе - relation (cause noble), поэтому seq_end.
 */
bool __fastcall nobleActionApplyProbe(const void* thisptr,
                                      int /* %edx */,
                                      IMidgardObjectMap* objectMap,
                                      void* sender,
                                      void* triggerers)
{
    const auto seq = nextSeq();
    NobleActionEffect effect{};
    const bool ok = readAt(thisptr, 0, effect);
    const auto player = ownerOf(objectMap, ok, &effect.stackId);

    const bool result = origNobleActionApply(thisptr, 0, objectMap, sender, triggerers);

    const auto seqEnd = nextSeq();
    countCall("noble_action");
    emit(seq,
         fmt::format(
             R"("ev":"noble_action","player":"{}","stack":"{}","target":"{}","action":{},"success":{},"value":{},"id":"{}","ret":{},"seq_end":{})",
             player, idOrNull(ok, effect.stackId), idOrNull(ok, effect.targetId),
             ok ? effect.categoryId : notComputed, ok && effect.success != 0,
             ok ? effect.value : notComputed, idOrNull(ok, effect.resultId), result, seqEnd));
    return result;
}

/* thief_cancel - 0x443506 */

using CancelNobleAction = bool(__stdcall*)(IMidgardObjectMap* objectMap,
                                           void* effects,
                                           const CMidgardID* stackId);
CancelNobleAction origCancelNobleAction{};

/**
 * Отмена действия вора сервером, __stdcall, ret 0xc: (map, список эффектов, stackId). Единственный
 * путь - разбор CNobleActionCancelMsg (0x41e7ab -> 0x44156d). Строит CEffectNobleActionCancel и
 * CEffectChangeAP - его ap_effect (cause thief_cancel) лежит внутри seq..seq_end.
 */
bool __stdcall cancelNobleActionProbe(IMidgardObjectMap* objectMap,
                                      void* effects,
                                      const CMidgardID* stackId)
{
    const auto seq = nextSeq();
    const auto player = ownerOf(objectMap, true, stackId);
    const auto stack = safeId(stackId);

    const bool result = origCancelNobleAction(objectMap, effects, stackId);

    const auto seqEnd = nextSeq();
    countCall("thief_cancel");
    emit(seq, fmt::format(R"("ev":"thief_cancel","player":"{}","stack":"{}","ret":{},"seq_end":{})",
                          player, stack, result, seqEnd));
    return result;
}

/* noble_choose - CEffectNobleChoose::apply 0x43a5a7 */

/** Раскладка по конструктору 0x43a4ca: вор +4, цель +8, Set<LNobleActionCat> +0xc. */
struct NobleChooseEffect
{
    const void* vftable;
    CMidgardID stackId;
    CMidgardID targetId;
};

constexpr std::size_t nobleSetOffset = 0xc;

EffectApply origNobleChooseApply{};

/**
 * Следующий узел дерева по порядку или nullptr, если что-то не читается. Шаг - тот же, что
 * ConstTreeIterator::operator++ (d2tree.h); узел: parent +4, left +8, right +0xc.
 */
const void* nextTreeNode(const void* node, const void* nil, int& steps)
{
    const void* right = nullptr;
    if (!readAt(node, 0xc, right))
        return nullptr;

    if (right != nil) {
        for (node = right;;) {
            const void* left = nullptr;
            if (++steps > maxTreeSteps || !readAt(node, 8, left))
                return nullptr;
            if (left == nil)
                return node;
            node = left;
        }
    }

    const void* parent = nullptr;
    for (;;) {
        const void* parentRight = nullptr;
        if (++steps > maxTreeSteps || !readAt(node, 4, parent) || !readAt(parent, 0xc, parentRight))
            return nullptr;
        if (node != parentRight)
            break;
        node = parent;
    }

    const void* nodeRight = nullptr;
    if (!readAt(node, 0xc, nodeRight))
        return nullptr;

    return nodeRight != parent ? parent : node;
}

/**
 * Id категорий множества JSON-массивом, по порядку дерева. game::Tree: голова +0xc, nil +0x14 от
 * начала множества; наименьший узел - head->left; значение узла на +0x10, у LNobleActionCat
 * {vftable, table, id} id на +8 значения. Нечитаемый указатель обрывает обход - полноту строки
 * проверяют по полю count.
 */
std::string nobleActionsOf(const void* set)
{
    std::string result{"["};
    const void* head = nullptr;
    const void* nil = nullptr;
    const void* node = nullptr;
    if (!readAt(set, 0xc, head) || !readAt(set, 0x14, nil) || !readAt(head, 8, node))
        return result + ']';

    const char* separator = "";
    int steps = 0;
    for (int count = 0; node && node != head && count < maxNobleActions; ++count) {
        int id = notComputed;
        if (!readAt(node, 0x18, id))
            break;

        result += fmt::format("{}{}", separator, id);
        separator = ",";
        node = nextTreeNode(node, nil, steps);
    }

    return result + ']';
}

/**
 * Подход вора к цели (обработчик 0x442de5): множество действий из серверного списка 0x5d32b8 - с
 * калиткой союзника, которой нет в интерфейсном. Пустое множество и следом CEffectKillStack -
 * неподкупный лидер цели; пустое без него - отказ всем списком со списанием хода (ap_effect cause
 * thief_no_actions). Одна строка на подход: это эффект сервера, интерфейс его не опрашивает.
 */
bool __fastcall nobleChooseApplyProbe(const void* thisptr,
                                      int /* %edx */,
                                      IMidgardObjectMap* objectMap,
                                      void* sender,
                                      void* triggerers)
{
    const auto seq = nextSeq();
    NobleChooseEffect effect{};
    const bool ok = readAt(thisptr, 0, effect);
    const auto player = ownerOf(objectMap, ok, &effect.stackId);
    const auto* set = ok ? static_cast<const char*>(thisptr) + nobleSetOffset : nullptr;
    std::uint32_t length = 0;
    const bool hasLength = readAt(set, 4, length); // длину так же читает сам движок, 0x442f7e
    const auto actions = nobleActionsOf(set);

    const bool result = origNobleChooseApply(thisptr, 0, objectMap, sender, triggerers);

    countCall("noble_choose");
    emit(seq,
         fmt::format(
             R"("ev":"noble_choose","player":"{}","stack":"{}","target":"{}","count":{},"actions":{},"ret":{})",
             player, idOrNull(ok, effect.stackId), idOrNull(ok, effect.targetId),
             hasLength ? static_cast<std::int64_t>(length) : std::int64_t{notComputed}, actions,
             result));
    return result;
}

/* alliance_propose - 0x43b5c8, alliance_change - 0x43574e, trade_accept - 0x433805 */

/** Пара игроков эффекта: +4 и +8 (конструкторы 0x43b57e, 0x4356f0, 0x433798). */
struct PlayerPairEffect
{
    const void* vftable;
    CMidgardID playerA;
    CMidgardID playerB;
};

EffectApply origProposeAllianceApply{};

/**
 * CEffectProposeAlliance::apply: результат canMakeAlliance (0x5daa0e) не проверяется, сообщение
 * CCmdProposeAllianceMsg уходит игроку b - значит a предлагает, b - адресат (ранг 2). Отношения
 * предложение не двигает; relation - пара до вызова.
 */
bool __fastcall proposeAllianceApplyProbe(const void* thisptr,
                                          int /* %edx */,
                                          IMidgardObjectMap* objectMap,
                                          void* sender,
                                          void* triggerers)
{
    const auto seq = nextSeq();
    PlayerPairEffect effect{};
    const bool ok = readAt(thisptr, 0, effect);
    const auto fields = fmt::format(
        R"("a":"{}","b":"{}","human_a":{},"human_b":{},"race_a":{},"race_b":{},"relation":{})",
        idOrNull(ok, effect.playerA), idOrNull(ok, effect.playerB),
        humanOf(objectMap, ok, &effect.playerA), humanOf(objectMap, ok, &effect.playerB),
        ok ? playerRace(objectMap, &effect.playerA) : notComputed,
        ok ? playerRace(objectMap, &effect.playerB) : notComputed,
        ok ? playerPairRelation(objectMap, &effect.playerA, &effect.playerB) : "null");

    const bool result = origProposeAllianceApply(thisptr, 0, objectMap, sender, triggerers);

    countCall("alliance_propose");
    emit(seq, fmt::format(R"("ev":"alliance_propose",{},"ret":{})", fields, result));
    return result;
}

/** CEffectChangeAlliance: пара +4/+8 и три байта +0xc/+0xd/+0xe. */
struct ChangeAllianceEffect
{
    const void* vftable;
    CMidgardID playerA;
    CMidgardID playerB;
    std::uint8_t accept;
    std::uint8_t silent; // без D_REF_ALLY и сообщения
    std::uint8_t flagE;  // уходит в 0x5d91af
};

EffectApply origChangeAllianceApply{};

/**
 * CEffectChangeAlliance::apply. accept - союз (alliance_make); иначе союзники - разрыв
 * (alliance_break); иначе, если не silent, - отказ (alliance_refuse). Строка relation - внутри
 * seq..seq_end; отказ двух людей мутатор не зовёт, и виден только здесь.
 */
bool __fastcall changeAllianceApplyProbe(const void* thisptr,
                                         int /* %edx */,
                                         IMidgardObjectMap* objectMap,
                                         void* sender,
                                         void* triggerers)
{
    const auto seq = nextSeq();
    ChangeAllianceEffect effect{};
    const bool ok = readAt(thisptr, 0, effect);
    const auto fields = fmt::format(
        R"("a":"{}","b":"{}","accept":{},"silent":{},"flag_e":{},"human_a":{},"human_b":{},"before":{})",
        idOrNull(ok, effect.playerA), idOrNull(ok, effect.playerB), ok && effect.accept != 0,
        ok && effect.silent != 0, ok && effect.flagE != 0,
        humanOf(objectMap, ok, &effect.playerA), humanOf(objectMap, ok, &effect.playerB),
        ok ? playerPairRelation(objectMap, &effect.playerA, &effect.playerB) : "null");

    const bool result = origChangeAllianceApply(thisptr, 0, objectMap, sender, triggerers);

    const auto seqEnd = nextSeq();
    const auto after = ok ? playerPairRelation(objectMap, &effect.playerA, &effect.playerB)
                          : std::string{"null"};
    countCall("alliance_change");
    emit(seq, fmt::format(R"("ev":"alliance_change",{},"after":{},"ret":{},"seq_end":{})", fields,
                          after, result, seqEnd));
    return result;
}

/** CEffectAcceptTrade (конструктор 0x433798). */
struct AcceptTradeEffect
{
    const void* vftable;
    CMidgardID playerA;  // +4, платит золото
    CMidgardID playerB;  // +8, получает золото
    CMidgardID objectId; // +0xc, предмет или заклинание
    CMidgardID id2;      // +0x10
    int gold;            // +0x14, доплата
    std::uint8_t accepted; // +0x18
};

EffectApply origAcceptTradeApply{};

/**
 * CEffectAcceptTrade::apply: при accepted - обмен предмета (0x5d90d7) или заклинания (0x5d9134) с
 * relation trade_* и bank внутри seq..seq_end; при отказе мутатор не зовётся. Цену объекта зонд
 * не считает (пришлось бы звать 0x600a20) - она из Gitems/Gspells.
 */
bool __fastcall acceptTradeApplyProbe(const void* thisptr,
                                      int /* %edx */,
                                      IMidgardObjectMap* objectMap,
                                      void* sender,
                                      void* triggerers)
{
    const auto seq = nextSeq();
    AcceptTradeEffect effect{};
    const bool ok = readAt(thisptr, 0, effect);

    const bool result = origAcceptTradeApply(thisptr, 0, objectMap, sender, triggerers);

    const auto seqEnd = nextSeq();
    countCall("trade_accept");
    emit(seq,
         fmt::format(
             R"("ev":"trade_accept","a":"{}","b":"{}","object":"{}","id2":"{}","gold":{},"accepted":{},"ret":{},"seq_end":{})",
             idOrNull(ok, effect.playerA), idOrNull(ok, effect.playerB),
             idOrNull(ok, effect.objectId), idOrNull(ok, effect.id2),
             ok ? effect.gold : notComputed, ok && effect.accepted != 0, result, seqEnd));
    return result;
}

} // namespace

} // namespace probe

namespace hooks {

void addNobleHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    const GogHook entries[] = {
        {"noble_action",     0x43a335, (void*)nobleActionApplyProbe,     (void**)&origNobleActionApply},
        {"thief_cancel",     0x443506, (void*)cancelNobleActionProbe,    (void**)&origCancelNobleAction},
        {"noble_choose",     0x43a5a7, (void*)nobleChooseApplyProbe,     (void**)&origNobleChooseApply},
        {"alliance_propose", 0x43b5c8, (void*)proposeAllianceApplyProbe, (void**)&origProposeAllianceApply},
        {"alliance_change",  0x43574e, (void*)changeAllianceApplyProbe,  (void**)&origChangeAllianceApply},
        {"trade_accept",     0x433805, (void*)acceptTradeApplyProbe,     (void**)&origAcceptTradeApply},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
}

} // namespace hooks
