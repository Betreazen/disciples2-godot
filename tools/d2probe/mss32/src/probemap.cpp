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
#include "d2list.h"
#include "fortification.h"
#include "game.h"
#include "gameutils.h"
#include "midgardid.h"
#include "midgardobjectmap.h"
#include "midstack.h"
#include "midunit.h"
#include "mqpoint.h"
#include "scenarioinfo.h"
#include <algorithm>
#include <cstdint>
#include <fmt/format.h>
#include <intrin.h>
#include <string>
#include <vector>
#include <Windows.h>

/*
 * Схема корпуса 6.0, партия 1: ось времени сохранения, захват деревни, рост земли, вход в сайт,
 * снятие просроченных заклинаний, создание предметов и два эффекта карты - очки хода и HP.
 *
 * Адреса - game-pristine/Discipl2.exe (Gog 3.01, ImageBase 0x400000). Разбор каждой точки -
 * disciples-modding/.superpowers/sdd/2026-09-14-probe-v6/spec-map.md и spec-thieves-diplo.md;
 * пролог каждой цели (5 байт без ветвлений внутрь), ширина ret и слот vftable у эффектов сверены
 * capstone ещё раз перед записью сюда. Все хуки только читают: оригинал зовётся как есть, его
 * ответ возвращается без изменений. Поля объектов снимаются readAt, то есть через readable().
 */

namespace probe {

namespace {

using namespace game;

/** Absent or unreadable values are journaled as this, never as a plausible number. */
constexpr int notComputed = -1;

/** Потолок длины имени сохранения: MAX_PATH, длиннее _makepath всё равно не соберёт. */
constexpr std::size_t maxSaveName = 260;

/** Потолок узлов при обходе списка клеток роста, если квота сама не годится в потолок. */
constexpr std::uint32_t maxWalkCells = 4096;

/** Сигнатура IEventEffect::apply всех эффектов карты: __thiscall, ret 0xc. */
using EffectApply = bool(__fastcall*)(const void* thisptr,
                                      int edx,
                                      IMidgardObjectMap* objectMap,
                                      void* sender,
                                      void* triggerers);

/** Адрес возврата -> причина, для вызывающих, найденных сканированием .text на E8. */
struct CallSite
{
    std::uintptr_t returnAddress;
    const char* cause;
};

template <std::size_t N>
const char* causeOf(const CallSite (&sites)[N], std::uintptr_t returnAddress)
{
    for (const auto& site : sites) {
        if (site.returnAddress == returnAddress)
            return site.cause;
    }

    return "unknown";
}

std::string idOrNull(bool readableValue, const CMidgardID& id)
{
    return readableValue ? idToString(&id) : std::string{"null"};
}

} // namespace

/** Кодовая страница строк игры (имена сохранений, сценариев, тексты). */
constexpr unsigned gameCodePage = 1251;

/** Байты cp1251 как UTF-8 (MultiByteToWideChar -> WideCharToMultiByte). Пусто при сбое. */
std::string utf8FromGameText(const std::string& bytes)
{
    if (bytes.empty())
        return {};

    const int size = static_cast<int>(bytes.size());
    const int wideLength = MultiByteToWideChar(gameCodePage, 0, bytes.data(), size, nullptr, 0);
    if (wideLength <= 0)
        return {};

    std::wstring wide(static_cast<std::size_t>(wideLength), L'\0');
    MultiByteToWideChar(gameCodePage, 0, bytes.data(), size, wide.data(), wideLength);

    const int length = WideCharToMultiByte(CP_UTF8, 0, wide.data(), wideLength, nullptr, 0,
                                           nullptr, nullptr);
    if (length <= 0)
        return {};

    std::string result(static_cast<std::size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.data(), wideLength, result.data(), length, nullptr,
                        nullptr);
    return result;
}

/**
 * C-строка игры (cp1251) как тело JSON-строки в UTF-8: кавычка и обратная косая экранируются,
 * управляющие байты - \u00XX, остальное - байты UTF-8 как есть. Читаемость проверяется на первой и
 * на каждой новой странице, так что строка у края выделенной памяти обрывается, а не роняет игру.
 */
std::string jsonCString(const char* text, std::size_t maxLength)
{
    std::string bytes;
    for (std::size_t i = 0; i < maxLength; ++i) {
        const auto* p = text + i;
        const bool pageStart = (reinterpret_cast<std::uintptr_t>(p) & 0xfff) == 0;
        if ((i == 0 || pageStart) && !readable(p, 1))
            break;

        if (*p == 0)
            break;

        bytes += *p;
    }

    std::string result;
    for (const char ch : utf8FromGameText(bytes)) {
        const auto c = static_cast<unsigned char>(ch);
        if (c == '"' || c == '\\') {
            result += '\\';
            result += ch;
        } else if (c < 0x20) {
            result += fmt::format("\\u{:04x}", c);
        } else {
            result += ch;
        }
    }

    return result;
}

namespace {

/* ---------------------------------------------------------------------------------------------
 * save - 0x429f3f
 */

using SaveGame = bool(__fastcall*)(void* thisptr,
                                   int edx,
                                   const char* name,
                                   void* a2,
                                   const char* tempSource,
                                   void* a4,
                                   void* a5);
SaveGame origSaveGame{};

/**
 * CMidServerLogicCore::saveGame, 0x429f3f, __thiscall, ret 0x14 - единственный писатель файла
 * сохранения (обработчик CSaveGameMsg 0x429f07, единственный вызов 0x429f35; туда же приходят
 * диалог сохранения, QuickSave и имя из CStratInterf). Слот 1 - имя (_splitpath/_makepath с "sg"),
 * слот 3 - строка: непустая даёт временное имя через GetTempFileNameA с префиксом "D2_"; слоты
 * 2, 4, 5 зонд не трогает. Карта объектов - [[this+4]+0x14], тот же геттер 0x5a77e8. Запись файла
 * синхронна внутри вызова: всё с seq меньше строки save в файле уже есть, всё с seq больше
 * seq_end - ещё нет.
 */
bool __fastcall saveGameProbe(void* thisptr,
                              int /* %edx */,
                              const char* name,
                              void* a2,
                              const char* tempSource,
                              void* a4,
                              void* a5)
{
    const auto seq = nextSeq();
    const auto nameText = jsonCString(name, maxSaveName);
    const bool temp = readable(tempSource, 1) && tempSource[0] != 0;

    int turn = notComputed;
    const void* core = nullptr;
    const IMidgardObjectMap* objectMap = nullptr;
    if (readAt(thisptr, 4, core) && readAt(core, 0x14, objectMap) && readableMap(objectMap)) {
        const auto* info = hooks::getScenarioInfo(objectMap);
        if (readable(info, sizeof(CScenarioInfo)))
            turn = info->currentTurn;
    }

    const bool result = origSaveGame(thisptr, 0, name, a2, tempSource, a4, a5);

    const auto seqEnd = nextSeq();
    countCall("save");
    emit(seq,
         fmt::format(R"("ev":"save","name":"{}","temp":{},"turn":{},"ok":{},"seq_end":{})",
                     nameText, temp, turn, result, seqEnd));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * fort_capture - CEffectCapture::apply 0x433e2e
 */

/** Поля CEffectCapture (типы доказаны аргументами dynamic_cast в apply). */
struct CaptureEffect
{
    const void* vftable;
    CMidgardID stackId;          // +4
    CMidgardID fortId;           // +8, CMidVillage
    CMidgardID ownerId;          // +0xc, новый владелец
    CMidgardID subraceId;        // +0x10
    std::uint8_t skipLeaderCheck; // +0x14
};

EffectApply origCaptureApply{};

/** Владелец, подраса и отряд-визитёр укрепления (CFortification +0x84/+0x88/+0x8c). */
struct FortOwnership
{
    std::string owner{"null"};
    std::string subrace{"null"};
    std::string visitor{"null"};
};

FortOwnership fortOwnership(const IMidgardObjectMap* objectMap, const CMidgardID* fortId)
{
    FortOwnership result;
    if (!readableMap(objectMap) || !readable(fortId, sizeof(CMidgardID)))
        return result;

    const auto* fort = hooks::getFort(objectMap, fortId);
    if (!readable(fort, sizeof(CFortification)))
        return result;

    result.owner = idToString(&fort->ownerId);
    result.subrace = idToString(&fort->subraceId);
    result.visitor = idToString(&fort->stackId);
    return result;
}

/**
 * Попытка входа в чужую деревню - боевая и бескровная. Выход без смены владельца: в деревне
 * стоит отряд (0x433eb8), жив гарнизон (0x433ecd), лидер - призванный или вор при сброшенном
 * байте +0x14 (0x433ef4...0x433f57). Смена - 0x5e724a, и внутри неё разовый обход земли
 * (land_grow лежит между seq и seq_end). Исход читается сравнением owner_after с owner_before.
 */
bool __fastcall captureApplyProbe(const void* thisptr,
                                  int /* %edx */,
                                  IMidgardObjectMap* objectMap,
                                  void* sender,
                                  void* triggerers)
{
    const auto seq = nextSeq();
    CaptureEffect effect{};
    const bool ok = readAt(thisptr, 0, effect);
    const auto before = ok ? fortOwnership(objectMap, &effect.fortId) : FortOwnership{};

    const bool result = origCaptureApply(thisptr, 0, objectMap, sender, triggerers);

    const auto seqEnd = nextSeq();
    // Укрепление ищется заново: визитор смены владельца пишет через карту для изменения, и
    // указатель, взятый до вызова, мог устареть.
    const auto after = ok ? fortOwnership(objectMap, &effect.fortId) : FortOwnership{};

    countCall("fort_capture");
    emit(seq,
         fmt::format(
             R"("ev":"fort_capture","stack":"{}","fort":"{}","owner":"{}","subrace":"{}","skip_leader_check":{},"owner_before":"{}","subrace_before":"{}","visitor_before":"{}","owner_after":"{}","ret":{},"seq_end":{})",
             idOrNull(ok, effect.stackId), idOrNull(ok, effect.fortId),
             idOrNull(ok, effect.ownerId), idOrNull(ok, effect.subraceId),
             ok && effect.skipLeaderCheck != 0, before.owner, before.subrace, before.visitor,
             after.owner, result, seqEnd));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * land_grow - 0x5e0551, land_reject - 0x5e07b2
 */

using TerrainWalk = bool(__stdcall*)(List<CMqPoint>* out,
                                     const IMidgardObjectMap* objectMap,
                                     const CMqPoint* center,
                                     int quota,
                                     const void* terrain);
TerrainWalk origTerrainWalk{};

/** Клетки выходного списка по порядку - это порядок взятия: "x:y;x:y". Пишет длину в 'count'. */
std::string cellsOf(const List<CMqPoint>* list, int quota, std::int64_t* count)
{
    *count = notComputed;
    if (!readable(list, sizeof(List<CMqPoint>)))
        return {};

    *count = list->length;
    const auto* head = list->head;
    if (!readable(head, sizeof(ListNode<CMqPoint>)))
        return {};

    // Длиннее квоты движок список не строит (0x5e0b5e): quota + 1 узлов - потолок против битого
    // списка, а сам потолок ограничен ещё и на случай нелепой квоты.
    const auto quotaCap = quota >= 0 && static_cast<std::uint32_t>(quota) < maxWalkCells
                              ? static_cast<std::uint32_t>(quota) + 1
                              : maxWalkCells;
    const auto cap = std::min<std::uint32_t>(list->length, quotaCap);

    std::string cells;
    auto* node = head->next;
    for (std::uint32_t i = 0; i < cap && node != head; ++i) {
        if (!readable(node, sizeof(ListNode<CMqPoint>)))
            break;

        cells += fmt::format("{}{}:{}", i ? ";" : "", node->data.x, node->data.y);
        node = node->next;
    }

    return cells;
}

/**
 * Обход роста земли, 0x5e0551, __stdcall, ret 0x14: (out, map, center, quota, terrain). Один
 * вызывающий - 0x5da283 (адрес возврата всегда 0x5da2b3), через него идут дневной рост
 * (CEffectTerrainCity), награда за захват (0x4340ea) и постановка жезла (0x437723); источник
 * харнесс узнаёт по центру и по окну fort_capture/rod_drop. Список живёт в кадре вызывающего и
 * после возврата ещё цел.
 */
bool __stdcall terrainWalkProbe(List<CMqPoint>* out,
                                const IMidgardObjectMap* objectMap,
                                const CMqPoint* center,
                                int quota,
                                const void* terrain)
{
    const auto seq = nextSeq();
    CMqPoint origin{notComputed, notComputed};
    readAt(center, 0, origin);
    // LTerrainCategory {vftable, table, id}: конструктор обхода кладёт [terrain+8] в +0x18.
    int terrainId = notComputed;
    readAt(terrain, 8, terrainId);

    const bool result = origTerrainWalk(out, objectMap, center, quota, terrain);

    const auto seqEnd = nextSeq();
    std::int64_t count = notComputed;
    const auto cells = cellsOf(out, quota, &count);

    countCall("land_grow");
    emit(seq,
         fmt::format(
             R"("ev":"land_grow","x":{},"y":{},"quota":{},"terrain":{},"count":{},"cells":"{}","ret":{},"seq_end":{})",
             origin.x, origin.y, quota, terrainId, count, cells, result, seqEnd));
    return result;
}

/** Объект обхода (конструктор 0x5e0607): карта +0, центр +4/+8, квота +0xc. */
struct TerrainWalker
{
    const IMidgardObjectMap* objectMap;
    CMqPoint center;
    int quota;
};

using AcceptCell = bool(__fastcall*)(const void* thisptr, int edx, const CMqPoint* point);
AcceptCell origAcceptCell{};

/**
 * Приём клетки обходом, 0x5e07b2, __thiscall, ret 4. Отвергают по порядку: край карты, два
 * грунта, чужой жезл, жила, окрестность 0x5e0959, чужое укрепление, блокирующий ландмарк.
 * Строка - только на отказ; счётчик - на каждый вызов, без бюджета. Вложенных строк у приёма
 * нет, поэтому seq берётся после оригинала и только для строки: номер на каждый принятый вызов
 * был бы пустым разрывом.
 */
bool __fastcall acceptCellProbe(const void* thisptr, int /* %edx */, const CMqPoint* point)
{
    const bool result = origAcceptCell(thisptr, 0, point);

    countCall("land_reject");
    if (result)
        return result;

    TerrainWalker walker{nullptr, {notComputed, notComputed}, notComputed};
    readAt(thisptr, 0, walker);
    CMqPoint cell{notComputed, notComputed};
    readAt(point, 0, cell);

    emit(nextSeq(),
         fmt::format(R"("ev":"land_reject","x":{},"y":{},"cx":{},"cy":{},"quota":{})", cell.x,
                     cell.y, walker.center.x, walker.center.y, walker.quota));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * site_visit - 0x5d7664, spell_expire - CEffectRemoveSpell::apply 0x43b9ef
 */

using VisitSite = bool(__stdcall*)(const IMidgardObjectMap* objectMap,
                                   const CMidgardID* siteId,
                                   const CMidgardID* stackId);
VisitSite origVisitSite{};

/**
 * Вход в сайт, 0x5d7664, __stdcall, ret 0xc; единственный вызов - CEffectVisit::apply (0x43f516).
 * Посетитель добавляется по владельцу отряда, повтор не запрещён: при членстве тоже true.
 * Отряд снимается до вызова: move_left - очки хода на входе.
 */
bool __stdcall visitSiteProbe(const IMidgardObjectMap* objectMap,
                              const CMidgardID* siteId,
                              const CMidgardID* stackId)
{
    const auto seq = nextSeq();
    const auto* stack = readableStack(objectMap, stackId);
    const auto owner = stack ? idToString(&stack->ownerId) : std::string{"null"};
    const int moveLeft = stack ? static_cast<int>(stack->movement) : notComputed;

    const bool result = origVisitSite(objectMap, siteId, stackId);

    countCall("site_visit");
    emit(seq,
         fmt::format(
             R"("ev":"site_visit","site":"{}","stack":"{}","owner":"{}","move_left":{},"ret":{})",
             safeId(siteId), safeId(stackId), owner, moveLeft, result));
    return result;
}

/** CEffectRemoveSpell (конструктор 0x43b950): игрок +4, объект хода +8 - первые 4 байта число. */
struct RemoveSpellEffect
{
    const void* vftable;
    CMidgardID playerId;
    int turn;
};

EffectApply origRemoveSpellApply{};

/**
 * Снятие просроченных CMidSpellCast и CMidSpellEffects игрока, условие turn >= TURN_STOP.
 * Строится в начале хода игрока (0x443d01); apply зовёт ещё диспетчер 0x430c9a.
 */
bool __fastcall removeSpellApplyProbe(const void* thisptr,
                                      int /* %edx */,
                                      IMidgardObjectMap* objectMap,
                                      void* sender,
                                      void* triggerers)
{
    const auto seq = nextSeq();
    RemoveSpellEffect effect{};
    const bool ok = readAt(thisptr, 0, effect);

    const bool result = origRemoveSpellApply(thisptr, 0, objectMap, sender, triggerers);

    const auto seqEnd = nextSeq();
    countCall("spell_expire");
    emit(seq, fmt::format(R"("ev":"spell_expire","player":"{}","turn":{},"ret":{},"seq_end":{})",
                          idOrNull(ok, effect.playerId), ok ? effect.turn : notComputed, result,
                          seqEnd));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * item_create - 0x5e732f
 */

using CreateItem = bool(__stdcall*)(const CMidgardID* id1,
                                    const CMidgardID* id2,
                                    const IMidgardObjectMap* objectMap,
                                    int applySlot);
CreateItem origCreateItem{};

/** Шесть вызовов 0x5e732f в .text; первые три опознаны не до конца (ранг 3). */
constexpr CallSite itemCreateSites[] = {
    {0x421ef5, "inventory_0x421dab"}, // цикл по инвентарю <- 0x42e285 <- 0x42e001
    {0x42f18d, "command_0x42f133"},   // рядом CCommandMsgTempl<0x2A>
    {0x42f86d, "scenario_start"},     // 0x42f84f <- 0x42f359, рядом CImportLeaderStore
    {0x43866d, "give_item"},          // CEffectGiveItem::apply
    {0x43915f, "loot_ruin"},          // CEffectLootRuin::apply
    {0x5d773a, "site_buy_item"},      // onSiteBuyItem
};

/**
 * Создание предмета, 0x5e732f, __stdcall, ret 0x10: (id1, id2, map, apply) - CVisitorCreateItem с
 * +8 = *id1, +0xc = *id2. Роли id не читаны, поэтому поля названы по типу id (правило схемы 5).
 */
bool __stdcall createItemProbe(const CMidgardID* id1,
                               const CMidgardID* id2,
                               const IMidgardObjectMap* objectMap,
                               int applySlot)
{
    const auto seq = nextSeq();
    const auto returnAddress = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    const void* ids[] = {id1, id2};
    const auto idFields = idsField(ids, 2);

    const bool result = origCreateItem(id1, id2, objectMap, applySlot);

    const auto seqEnd = nextSeq();
    countCall("item_create");
    emit(seq, fmt::format(
                  R"("ev":"item_create",{},"apply":{},"ret_addr":"{}","cause":"{}","ret":{},"seq_end":{})",
                  idFields, (applySlot & 0xff) != 0, hexAddress(returnAddress),
                  causeOf(itemCreateSites, returnAddress), result, seqEnd));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ap_effect - конструктор CEffectChangeAP 0x43588c, ap_apply - CEffectChangeAP::apply 0x4358d4
 */

/** CEffectChangeAP: отряд +4, дельта +8 (конструктор 0x43589a, 0x4358a1). */
struct ChangeApEffect
{
    const void* vftable;
    CMidgardID stackId;
    int delta;
};

/** Девять вызовов конструктора в .text; дельта у всех, кроме двух последних, - 0x5ce239. */
constexpr CallSite changeApSites[] = {
    {0x440850, "attack"},           // обработчик приказа 0x440667
    {0x440aba, "enter_fort"},       // шаг на клетку укрепления, 0x4408cb
    {0x44079e, "illusion_target"},  // цель-иллюзия, CEffectKillStack
    {0x41d8ca, "duel"},             // CNobleActionResultDuel, перед CEffectBattle
    {0x442fb1, "thief_no_actions"}, // пустой список действий вора (0x442f7e)
    {0x4431a5, "thief_roll"},       // вор после броска, кроме Дуэли
    {0x443591, "thief_cancel"},     // отмена действия вора
    {0x4441c8, "riot_turn_start"},  // начало хода: отряд в бунтующей деревне, дельта 0x5ed035
    {0x41c6d3, "thief_0x41c5e3"},   // 0x41c5e3 <- 0x443955, дельта -esi, не опознан
};

using ChangeApCtor = void*(__fastcall*)(void* thisptr,
                                        int edx,
                                        const IMidgardObjectMap* objectMap,
                                        const CMidgardID* stackId,
                                        int delta);
ChangeApCtor origChangeApCtor{};
EffectApply origChangeApApply{};

/** Дельта как JSON: число или null - у неё отрицательные значения законны, -1 выдумкой не отличить. */
std::string deltaOf(bool ok, const ChangeApEffect& effect)
{
    return ok ? fmt::format("{}", effect.delta) : std::string{"null"};
}

/**
 * Конструктор, __thiscall, ret 0xc: (map, stackId*, delta). Поля читаются после оригинала - до
 * него объект не построен. Причина - по адресу возврата; пара с ap_apply - по полю effect.
 */
void* __fastcall changeApCtorProbe(void* thisptr,
                                   int /* %edx */,
                                   const IMidgardObjectMap* objectMap,
                                   const CMidgardID* stackId,
                                   int delta)
{
    const auto seq = nextSeq();
    const auto returnAddress = reinterpret_cast<std::uintptr_t>(_ReturnAddress());

    const auto result = origChangeApCtor(thisptr, 0, objectMap, stackId, delta);

    ChangeApEffect effect{};
    const bool ok = readAt(thisptr, 0, effect);
    countCall("ap_effect");
    emit(seq,
         fmt::format(
             R"("ev":"ap_effect","effect":"{}","stack":"{}","delta":{},"ret_addr":"{}","cause":"{}")",
             hexAddress(reinterpret_cast<std::uintptr_t>(thisptr)), idOrNull(ok, effect.stackId),
             deltaOf(ok, effect), hexAddress(returnAddress),
             causeOf(changeApSites, returnAddress)));
    return result;
}

int movementOf(const IMidgardObjectMap* objectMap, bool ok, const ChangeApEffect& effect)
{
    const auto* stack = ok ? readableStack(objectMap, &effect.stackId) : nullptr;
    return stack ? static_cast<int>(stack->movement) : notComputed;
}

/** Применение: CVisitorChangeStackMoveAllowance 0x5e780d, movement += delta в [0, MOVE]. */
bool __fastcall changeApApplyProbe(const void* thisptr,
                                   int /* %edx */,
                                   IMidgardObjectMap* objectMap,
                                   void* sender,
                                   void* triggerers)
{
    const auto seq = nextSeq();
    ChangeApEffect effect{};
    const bool ok = readAt(thisptr, 0, effect);
    const int moveBefore = movementOf(objectMap, ok, effect);

    const bool result = origChangeApApply(thisptr, 0, objectMap, sender, triggerers);

    const auto seqEnd = nextSeq();
    countCall("ap_apply");
    emit(seq,
         fmt::format(
             R"("ev":"ap_apply","effect":"{}","stack":"{}","delta":{},"move_before":{},"move_after":{},"ret":{},"seq_end":{})",
             hexAddress(reinterpret_cast<std::uintptr_t>(thisptr)), idOrNull(ok, effect.stackId),
             deltaOf(ok, effect), moveBefore, movementOf(objectMap, ok, effect), result, seqEnd));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * hp_change - CEffectChangeHP::apply 0x436299 (общий с партией «лидеры, предметы, бой»)
 */

/** CEffectChangeHP (конструктор 0x43624a): юнит +4, величина +8 (положительная - урон), байт +0xc. */
struct ChangeHpEffect
{
    const void* vftable;
    CMidgardID unitId;
    int amount;
    std::uint8_t flag;
};

EffectApply origChangeHpApply{};

int unitHp(const IMidgardObjectMap* objectMap, bool ok, const ChangeHpEffect& effect)
{
    if (!ok || !readableMap(objectMap))
        return notComputed;

    const auto* unit = gameFunctions().findUnitById(objectMap, &effect.unitId);
    return readable(unit, sizeof(CMidUnit)) ? unit->currentHp : notComputed;
}

/**
 * Изменение HP юнита на карте: 0x5e75f3(unit, -amount, 1). Создатели - результаты заклинаний
 * (0x41c192, 0x41cccb), отравление (0x41d486), Assassinate (0x41db63), взятка (0x41dccc) и
 * 0x44402b; в apply они не различимы, поэтому причины в строке нет.
 */
bool __fastcall changeHpApplyProbe(const void* thisptr,
                                   int /* %edx */,
                                   IMidgardObjectMap* objectMap,
                                   void* sender,
                                   void* triggerers)
{
    const auto seq = nextSeq();
    ChangeHpEffect effect{};
    const bool ok = readAt(thisptr, 0, effect);
    const int hpBefore = unitHp(objectMap, ok, effect);

    const bool result = origChangeHpApply(thisptr, 0, objectMap, sender, triggerers);

    const auto seqEnd = nextSeq();
    countCall("hp_change");
    emit(seq,
         fmt::format(
             R"("ev":"hp_change","unit":"{}","amount":{},"flag":{},"hp_before":{},"hp_after":{},"ret":{},"seq_end":{})",
             idOrNull(ok, effect.unitId), ok ? fmt::format("{}", effect.amount) : "null",
             ok ? static_cast<int>(effect.flag) : notComputed, hpBefore,
             unitHp(objectMap, ok, effect), result, seqEnd));
    return result;
}

} // namespace

} // namespace probe

namespace hooks {

void addMapHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    const GogHook entries[] = {
        {"save",         0x429f3f, (void*)saveGameProbe,         (void**)&origSaveGame},
        {"fort_capture", 0x433e2e, (void*)captureApplyProbe,     (void**)&origCaptureApply},
        {"land_grow",    0x5e0551, (void*)terrainWalkProbe,      (void**)&origTerrainWalk},
        {"land_reject",  0x5e07b2, (void*)acceptCellProbe,       (void**)&origAcceptCell},
        {"site_visit",   0x5d7664, (void*)visitSiteProbe,        (void**)&origVisitSite},
        {"spell_expire", 0x43b9ef, (void*)removeSpellApplyProbe, (void**)&origRemoveSpellApply},
        {"item_create",  0x5e732f, (void*)createItemProbe,       (void**)&origCreateItem},
        {"ap_effect",    0x43588c, (void*)changeApCtorProbe,     (void**)&origChangeApCtor},
        {"ap_apply",     0x4358d4, (void*)changeApApplyProbe,    (void**)&origChangeApApply},
        {"hp_change",    0x436299, (void*)changeHpApplyProbe,    (void**)&origChangeHpApply},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
}

} // namespace hooks
