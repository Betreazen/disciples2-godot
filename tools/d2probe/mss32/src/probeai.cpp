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
#include "currency.h"
#include "d2assert.h"
#include "gameutils.h"
#include "globaldata.h"
#include "globalvariables.h"
#include "midgardid.h"
#include "midgardobjectmap.h"
#include "midplayer.h"
#include "midstack.h"
#include "scenarioinfo.h"
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <fmt/format.h>
#include <intrin.h>
#include <iterator>
#include <mutex>
#include <string>
#include <vector>

/*
 * Схема корпуса 6.4: ход ИИ на карте.
 *
 * Адреса - game-dev/Discipl2.exe (Gog 3.01, ImageBase 0x400000), capstone; разбор -
 * disciples-modding/docs/synthetic-maps/analysis/J-map-ai.md и J-map-ai-notes.md. Хуки только читают:
 * оригинал зовётся с теми же аргументами, ответ возвращается без изменений. Броски - значения
 * generateRandomNumber из кольца зонда (rngResultAt) по номерам после rng_before: строка `rng` вне боя
 * не пишется.
 *
 * Дипломатия ИИ (CAiDiplomacyEval::evaluate 0x4486f5):
 *   - 0x448897 разрыв, __stdcall(map, CMidPlayer* свой, CMidPlayer* чужой), ret 0xc, bool;
 *   - 0x448aad предложение, __stdcall(map, CMidPlayer* чужой, int* rel), ret 0xc, bool. В прологе
 *     call 0x5f7e10 (rel > D_NEUTRAL) - Detours переносит E8 с поправкой цели; шанс - 0x448ae2
 *     (те же аргументы, ret 0xc, int), его хук только запоминает ответ;
 *   - 0x448bda подарок, __stdcall(map, id свой, id чужой, int* rel), ret 0x10, действие или nullptr.
 * Ответы ИИ (диспетчер команд 0x474fde): союз 0x474b37 __thiscall(flag, id предлагающего, id адресата),
 * ret 0xc, шанс 0x474c21 __stdcall(map, CMidPlayer* предлагающий, int* rel), ret 0xc; сделка 0x474841
 * __thiscall(flag, id предлагающего, id предмета/заклинания, ?, золото), ret 0x14, шанс 0x4749c8
 * __stdcall(золото, цена), ret 8. Отвечающий - this+8.
 * Стратегический слой: 0x44c825 - запись памяти исполненного действия, __thiscall(memo, id цели,
 * int* день, тип), ret 0xc, один вызов (0x44eab3); 0x44e777 - полный сброс курсоров, __thiscall(),
 * возвраты 0x44e757 (begin 0x44e738) и 0x44ead1 (после execute).
 * Ход: 0x4719f8 - старт состояний драйвера ИИ (__thiscall(a1, a2, a3), ret 0xc; [this] карта,
 * this+0x4c id игрока); EndTurn 0x41e229 __thiscall(id игрока), ret 4, возвраты 0x471bd5 (ИИ) и
 * 0x422e22 (команда человека).
 * Приказ: 0x5e811a __stdcall(id отряда, LOrderCategory*, id цели, flag, map, apply), ret 0x18, bool
 * (CVisitorChangeStackOrder); команда 0x41f21d __thiscall(4 аргумента), ret 0x10 - только ИИ.
 * Бюджет: 0x44661c - вставка в карту бюджетов __thiscall(out, int* тип, значение), ret 0xc; this =
 * объект бюджета + 0x20 (+4 id игрока, +8 казна на начало дня, +0x14 CMidPlayer::spellBank); значение -
 * {Bank бюджет, Bank, bool вычесть резерв +0x18}; зовёт только 0x446292 (0x446452).
 * Чат: 0x4733a4 __thiscall(id адресата, LAiMsgCategory*), ret 8; this+8 - id отправителя; номер
 * сообщения - rng(число строк TAiMsg расы в категории), rng(0) броска не тратит.
 */

namespace probe {

namespace {

using namespace game;

constexpr int notComputed = -1;

// D_BKA_CHANCE и D_BKA_TURN - те самые поля, что берут геттеры 0x5878dd (+0xe8) и 0x5878e5 (+0xec).
assert_offset(GlobalVariablesData, dBkaChance, 0xe8);
assert_offset(GlobalVariablesData, dBkaTurn, 0xec);

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

std::uintptr_t callerOf(void* returnAddress)
{
    return reinterpret_cast<std::uintptr_t>(returnAddress);
}

/** Id как JSON: строка в кавычках или null, если не читается. */
std::string jsonId(const void* id)
{
    CMidgardID value{};
    return readAt(id, 0, value) ? fmt::format(R"("{}")", idToString(&value)) : std::string{"null"};
}

/** Id объекта сценария (IMidScenarioObject: vftable, id +4) как JSON. */
std::string objectId(const void* object)
{
    return object ? jsonId(static_cast<const char*>(object) + 4) : std::string{"null"};
}

std::string jsonInt(bool known, long long value)
{
    return known ? std::to_string(value) : std::string{"null"};
}

/** Бросок номер k после 'before' как JSON: null - столько бросков не было, -1 - вытеснен из кольца. */
std::string rollAt(std::uint64_t before, std::uint64_t after, std::uint64_t k)
{
    return after >= before + k ? std::to_string(rngResultAt(before + k)) : std::string{"null"};
}

int intAt(const void* p)
{
    int value = notComputed;
    readAt(p, 0, value);
    return value;
}

int currentTurn(const IMidgardObjectMap* objectMap)
{
    if (!readableMap(objectMap))
        return notComputed;

    const auto* info = hooks::getScenarioInfo(objectMap);
    return readable(info, sizeof(CScenarioInfo)) ? info->currentTurn : notComputed;
}

/* ---------------------------------------------------------------------------------------------
 * Состояние хода: чей ход ИИ начался последним и счётчики стратегии с его начала.
 */

struct TurnState
{
    bool known{};
    CMidgardID player{};
    int turn{notComputed};
    long long passBegins{};
    long long passes{};
    long long steps{};
};

std::mutex turnMutex;
TurnState turnState;

TurnState turnSnapshot()
{
    std::lock_guard<std::mutex> lock{turnMutex};
    return turnState;
}

std::string turnPlayer()
{
    const auto state = turnSnapshot();
    return state.known ? jsonId(&state.player) : std::string{"null"};
}

/* ---------------------------------------------------------------------------------------------
 * ai_break_check - 0x448897
 */

using BreakCheck = bool(__stdcall*)(IMidgardObjectMap* objectMap,
                                    const CMidPlayer* player,
                                    const CMidPlayer* other);
BreakCheck origBreakCheck{};

/** 0x4489f6(map, player), __stdcall, ret 8: число крепостей (CFortification, владелец +0x84). */
using FortCount = int(__stdcall*)(const IMidgardObjectMap* objectMap, const CMidPlayer* player);
const auto fortCount = reinterpret_cast<FortCount>(0x4489f6);

struct BreakVars
{
    int turnBase{notComputed};
    int cap{notComputed};
};

BreakVars breakVars()
{
    const auto* global = GlobalDataApi::get().getGlobalData();
    if (!readable(global, sizeof(*global)) || !readable(*global, sizeof(GlobalData)))
        return {};

    const auto* vars = (*global)->globalVariables;
    if (!readable(vars, sizeof(GlobalVariables))
        || !readable(vars->data, sizeof(GlobalVariablesData)))
        return {};

    return {vars->data->dBkaTurn, vars->data->dBkaChance};
}

/** Удвоение шанса сильного - сеттер 0x5838d0: пишет, только если значение в [-100, 100]. */
int doubledChance(int chance)
{
    const int doubled = chance * 2;
    return doubled >= -100 && doubled <= 100 ? doubled : chance;
}

bool __stdcall breakCheckProbe(IMidgardObjectMap* objectMap,
                               const CMidPlayer* player,
                               const CMidPlayer* other)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto rngBefore = rngCount();

    const bool result = origBreakCheck(objectMap, player, other);

    const auto rngAfter = rngCount();
    const auto seqEnd = nextSeq();

    // 0x4489f6 только считает объекты карты - движок зовёт её в самой проверке.
    const bool playersOk = readableMap(objectMap) && readable(player, sizeof(CMidPlayer))
                           && readable(other, sizeof(CMidPlayer));
    const int fortsPlayer = playersOk ? fortCount(objectMap, player) : notComputed;
    const int fortsOther = playersOk ? fortCount(objectMap, other) : notComputed;
    const bool strong = playersOk && fortsPlayer > fortsOther;
    const int turn = currentTurn(objectMap);
    const auto vars = breakVars();
    const int turnEff = strong && vars.turnBase > 0 ? vars.turnBase / 2 : vars.turnBase;

    std::uint32_t word = 0;
    const bool hasWord = playersOk && playerPairWord(objectMap, &player->id, &other->id, word);
    // Ход союза - биты 15-29 со знаком (0x5f7940: shl 2, sar 0x11); бит 31 - разрыв запрещён.
    const int allyTurn = static_cast<std::int32_t>(word << 2) >> 17;
    const bool noBreak = (word >> 31) != 0;
    const bool dKnown = hasWord && turn >= 0;
    const int d = turn - allyTurn;

    std::string chance{"null"};
    if (dKnown && !noBreak && vars.turnBase >= 0 && vars.cap >= 0 && d > turnEff) {
        const int base = (std::min)(d - turnEff, vars.cap);
        chance = std::to_string(strong ? doubledChance(base) : base);
    }

    countCall("ai_break_check");
    emit(seq,
         fmt::format(
             R"("ev":"ai_break_check","player":"{}","other":"{}","forts_player":{},"forts_other":{},"strong":{},"turn":{},"ally_turn":{},"nobreak":{},"d":{},"t_base":{},"t_eff":{},"cap":{},"chance":{},"roll":{},"rng_before":{},"rng_after":{},"ret_addr":"{}","ret":{},"seq_end":{})",
             playersOk ? idToString(&player->id) : std::string{"null"},
             playersOk ? idToString(&other->id) : std::string{"null"}, fortsPlayer, fortsOther,
             strong, turn, jsonInt(hasWord, allyTurn),
             hasWord ? (noBreak ? "true" : "false") : "null", jsonInt(dKnown, d), vars.turnBase,
             turnEff, vars.cap, chance, rollAt(rngBefore, rngAfter, 1), rngBefore, rngAfter,
             hexAddress(returnAddress), result, seqEnd));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ai_offer_check - 0x448aad (+ шанс 0x448ae2)
 */

using OfferCheck = bool(__stdcall*)(IMidgardObjectMap* objectMap,
                                    const CMidPlayer* other,
                                    const int* relation);
OfferCheck origOfferCheck{};

using OfferChance = int(__stdcall*)(IMidgardObjectMap* objectMap,
                                    const CMidPlayer* other,
                                    const int* relation);
OfferChance origOfferChance{};

struct ChanceSlot
{
    bool seen{};
    int value{};
};

thread_local ChanceSlot* offerSlot{nullptr};

int __stdcall offerChanceProbe(IMidgardObjectMap* objectMap,
                               const CMidPlayer* other,
                               const int* relation)
{
    const int chance = origOfferChance(objectMap, other, relation);
    if (offerSlot)
        *offerSlot = {true, chance};
    return chance;
}

bool __stdcall offerCheckProbe(IMidgardObjectMap* objectMap,
                               const CMidPlayer* other,
                               const int* relation)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto rngBefore = rngCount();

    ChanceSlot slot;
    auto* saved = offerSlot;
    offerSlot = &slot;
    const bool result = origOfferCheck(objectMap, other, relation);
    offerSlot = saved;

    const auto rngAfter = rngCount();
    const auto seqEnd = nextSeq();

    countCall("ai_offer_check");
    emit(seq,
         fmt::format(
             R"("ev":"ai_offer_check","turn_player":{},"other":{},"rel":{},"turn":{},"chance":{},"roll":{},"rng_before":{},"rng_after":{},"ret_addr":"{}","ret":{},"seq_end":{})",
             turnPlayer(), objectId(other), intAt(relation), currentTurn(objectMap),
             jsonInt(slot.seen, slot.value), rollAt(rngBefore, rngAfter, 1), rngBefore, rngAfter,
             hexAddress(returnAddress), result, seqEnd));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ai_gift_check - 0x448bda
 */

using GiftCheck = void*(__stdcall*)(IMidgardObjectMap* objectMap,
                                    const CMidgardID* player,
                                    const CMidgardID* other,
                                    const int* relation);
GiftCheck origGiftCheck{};

/** Класс действия подарка по vftable (RTTI CAiDiploItem, CAiDiploSpell, CAiDiploGold). */
std::string giftAction(const void* action)
{
    if (!action)
        return {"null"};

    std::uintptr_t vftable = 0;
    if (!readAt(action, 0, vftable))
        return {R"("other")"};

    switch (vftable) {
    case 0x6cf5dc:
        return {R"("item")"};
    case 0x6cf5f4:
        return {R"("spell")"};
    case 0x6cf60c:
        return {R"("gold")"};
    default:
        return {R"("other")"};
    }
}

void* __stdcall giftCheckProbe(IMidgardObjectMap* objectMap,
                               const CMidgardID* player,
                               const CMidgardID* other,
                               const int* relation)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto rngBefore = rngCount();

    auto* result = origGiftCheck(objectMap, player, other, relation);

    const auto rngAfter = rngCount();
    const auto seqEnd = nextSeq();
    const int rel = intAt(relation);

    // Порог - как 0x448be9: rel*20/100 целочисленно.
    countCall("ai_gift_check");
    emit(seq,
         fmt::format(
             R"("ev":"ai_gift_check","player":{},"other":{},"rel":{},"threshold":{},"roll":{},"kind_roll":{},"action":{},"rng_before":{},"rng_after":{},"ret_addr":"{}","ret":{},"seq_end":{})",
             jsonId(player), jsonId(other), rel, rel * 20 / 100, rollAt(rngBefore, rngAfter, 1),
             rollAt(rngBefore, rngAfter, 2), giftAction(result), rngBefore, rngAfter,
             hexAddress(returnAddress), result != nullptr, seqEnd));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ai_answer - союз 0x474b37 (шанс 0x474c21), сделка 0x474841 (шанс 0x4749c8)
 */

struct AnswerScope
{
    bool seen{};
    int chance{};
    std::uint64_t rngAtChance{};
    int rel{notComputed};
    bool relSeen{};
    std::string proposer{"null"};
    int gold{};
    int price{};
};

thread_local AnswerScope* answerScope{nullptr};

/** Бросок сразу после шанса и ответ по нему - только если шанс был посчитан. */
struct AnswerRoll
{
    std::string roll{"null"};
    std::string accept{"null"};
};

AnswerRoll answerRoll(const AnswerScope& scope, std::uint64_t rngAfter)
{
    if (!scope.seen || rngAfter <= scope.rngAtChance)
        return {};

    const int roll = rngResultAt(scope.rngAtChance + 1);
    if (roll < 0)
        return {std::to_string(roll), "null"};
    return {std::to_string(roll), roll < scope.chance ? "true" : "false"};
}

void emitAnswer(std::uint64_t seq,
                const char* kind,
                const std::string& player,
                const std::string& proposer,
                const std::string& trade, // "gold":..,"price":..,"object":..
                const AnswerScope& scope,
                std::uint64_t rngBefore,
                std::uint64_t rngAfter,
                std::uintptr_t returnAddress,
                std::uint64_t seqEnd)
{
    const auto roll = answerRoll(scope, rngAfter);
    countCall("ai_answer");
    emit(seq,
         fmt::format(
             R"("ev":"ai_answer","kind":"{}","player":{},"proposer":{},"rel":{},{},"roll":{},"accept":{},"rng_before":{},"rng_after":{},"ret_addr":"{}","ret":{},"seq_end":{})",
             kind, player, proposer, jsonInt(scope.relSeen, scope.rel), trade, roll.roll,
             roll.accept, rngBefore, rngAfter, hexAddress(returnAddress),
             jsonInt(scope.seen, scope.chance), seqEnd));
}

using AllianceChance = int(__stdcall*)(IMidgardObjectMap* objectMap,
                                       const CMidPlayer* proposer,
                                       const int* relation);
AllianceChance origAllianceChance{};

int __stdcall allianceChanceProbe(IMidgardObjectMap* objectMap,
                                  const CMidPlayer* proposer,
                                  const int* relation)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto rngBefore = rngCount();
    const int chance = origAllianceChance(objectMap, proposer, relation);
    const auto rngAfter = rngCount();

    AnswerScope own;
    auto& scope = answerScope ? *answerScope : own;
    scope.seen = true;
    scope.chance = chance;
    scope.rngAtChance = rngAfter;
    scope.rel = intAt(relation);
    scope.relSeen = true;
    scope.proposer = objectId(proposer);

    // Без внешнего хука ответа строку пишет сам шанс: броска ответа он уже не увидит.
    if (!answerScope)
        emitAnswer(seq, "alliance", "null", scope.proposer,
                   R"("gold":null,"price":null,"object":null)", scope, rngBefore, rngAfter,
                   returnAddress, nextSeq());
    return chance;
}

using AnswerAlliance = void(__fastcall*)(void* thisptr,
                                         int /* %edx */,
                                         int flag,
                                         const CMidgardID* proposer,
                                         const CMidgardID* target);
AnswerAlliance origAnswerAlliance{};

void __fastcall answerAllianceProbe(void* thisptr,
                                    int /* %edx */,
                                    int flag,
                                    const CMidgardID* proposer,
                                    const CMidgardID* target)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto rngBefore = rngCount();

    AnswerScope scope;
    auto* saved = answerScope;
    answerScope = &scope;
    origAnswerAlliance(thisptr, 0, flag, proposer, target);
    answerScope = saved;

    const auto rngAfter = rngCount();
    // Флаг 0 - команда не этому ИИ: только счёт вызовов.
    if ((flag & 0xff) == 0) {
        countCall("ai_answer");
        return;
    }

    emitAnswer(seq, "alliance", jsonId(static_cast<const char*>(thisptr) + 8), jsonId(proposer),
               R"("gold":null,"price":null,"object":null)", scope, rngBefore, rngAfter,
               returnAddress, nextSeq());
}

using TradeChance = int(__stdcall*)(int gold, int price);
TradeChance origTradeChance{};

int __stdcall tradeChanceProbe(int gold, int price)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto rngBefore = rngCount();
    const int chance = origTradeChance(gold, price);
    const auto rngAfter = rngCount();

    AnswerScope own;
    auto& scope = answerScope ? *answerScope : own;
    scope.seen = true;
    scope.chance = chance;
    scope.rngAtChance = rngAfter;
    scope.gold = gold;
    scope.price = price;

    if (!answerScope)
        emitAnswer(seq, "trade", "null", "null",
                   fmt::format(R"("gold":{},"price":{},"object":null)", gold, price), scope,
                   rngBefore, rngAfter, returnAddress, nextSeq());
    return chance;
}

using AnswerTrade = void(__fastcall*)(void* thisptr,
                                      int /* %edx */,
                                      int flag,
                                      const CMidgardID* proposer,
                                      const CMidgardID* object,
                                      int arg4,
                                      int gold);
AnswerTrade origAnswerTrade{};

void __fastcall answerTradeProbe(void* thisptr,
                                 int /* %edx */,
                                 int flag,
                                 const CMidgardID* proposer,
                                 const CMidgardID* object,
                                 int arg4,
                                 int gold)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto rngBefore = rngCount();

    AnswerScope scope;
    auto* saved = answerScope;
    answerScope = &scope;
    origAnswerTrade(thisptr, 0, flag, proposer, object, arg4, gold);
    answerScope = saved;

    const auto rngAfter = rngCount();
    if ((flag & 0xff) == 0) {
        countCall("ai_answer");
        return;
    }

    // Цена известна только из шанса: отказ без броска (нет денег, заклинание известно) - price null.
    emitAnswer(seq, "trade", jsonId(static_cast<const char*>(thisptr) + 8), jsonId(proposer),
               fmt::format(R"("gold":{},"price":{},"object":{})", gold,
                           jsonInt(scope.seen, scope.price), jsonId(object)),
               scope, rngBefore, rngAfter, returnAddress, nextSeq());
}

/* ---------------------------------------------------------------------------------------------
 * ai_strategic_step - 0x44c825, ai_pass - 0x44e777
 */

using MemoSet = void(__fastcall*)(void* thisptr,
                                  int /* %edx */,
                                  const CMidgardID* id,
                                  const int* day,
                                  int type);
MemoSet origMemoSet{};

void __fastcall memoSetProbe(void* thisptr,
                             int /* %edx */,
                             const CMidgardID* id,
                             const int* day,
                             int type)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto rngBefore = rngCount();
    origMemoSet(thisptr, 0, id, day, type);
    const auto rngAfter = rngCount();

    TurnState state;
    {
        std::lock_guard<std::mutex> lock{turnMutex};
        ++turnState.steps;
        state = turnState;
    }

    countCall("ai_strategic_step");
    emit(seq,
         fmt::format(
             R"("ev":"ai_strategic_step","player":{},"id":{},"type":{},"turn":{},"pass":{},"step":{},"rng_before":{},"rng_after":{},"ret_addr":"{}")",
             state.known ? jsonId(&state.player) : std::string{"null"}, jsonId(id), type,
             intAt(day), state.passes + 1, state.steps, rngBefore, rngAfter,
             hexAddress(returnAddress)));
}

constexpr std::uintptr_t passBeginReturn = 0x44e757;   // CAiStrategic::begin 0x44e738
constexpr std::uintptr_t passExecuteReturn = 0x44ead1; // step 0x44e7e4 после execute

using PassReset = void(__fastcall*)(void* thisptr, int /* %edx */);
PassReset origPassReset{};

void __fastcall passResetProbe(void* thisptr, int /* %edx */)
{
    const auto returnAddress = callerOf(_ReturnAddress());
    origPassReset(thisptr, 0);

    countCall("ai_pass");
    std::lock_guard<std::mutex> lock{turnMutex};
    if (returnAddress == passBeginReturn)
        ++turnState.passBegins;
    else if (returnAddress == passExecuteReturn)
        ++turnState.passes;
}

/* ---------------------------------------------------------------------------------------------
 * ai_turn - 0x4719f8 (начало), EndTurn 0x41e229 (конец)
 */

constexpr CallSite endTurnSites[] = {
    {0x471bd5, "ai"},    // драйвер ИИ 0x471b39
    {0x422e22, "human"}, // команда клиента EndTurn
};

using TurnBegin = void(__fastcall*)(void* thisptr, int /* %edx */, int arg1, int arg2, int arg3);
TurnBegin origTurnBegin{};

void __fastcall turnBeginProbe(void* thisptr, int /* %edx */, int arg1, int arg2, int arg3)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto rngBefore = rngCount();

    const IMidgardObjectMap* objectMap = nullptr;
    readAt(thisptr, 0, objectMap);
    CMidgardID player{};
    const bool known = readAt(thisptr, 0x4c, player);
    const int turn = currentTurn(objectMap);
    {
        // До оригинала: вложенные строки хода (приказы, бюджет) уже видят игрока.
        std::lock_guard<std::mutex> lock{turnMutex};
        turnState = TurnState{known, player, turn};
    }

    origTurnBegin(thisptr, 0, arg1, arg2, arg3);

    const auto rngAfter = rngCount();
    countCall("ai_turn");
    emit(seq,
         fmt::format(
             R"("ev":"ai_turn","phase":"begin","player":{},"turn":{},"cause":"ai","arg1":{},"arg2":{},"arg3":{},"pass_begins":null,"passes":null,"steps":null,"rng_before":{},"rng_after":{},"ret_addr":"{}","ret":null,"seq_end":{})",
             known ? jsonId(&player) : std::string{"null"}, turn, arg1, arg2, arg3, rngBefore,
             rngAfter, hexAddress(returnAddress), nextSeq()));
}

using EndTurn = bool(__fastcall*)(void* thisptr, int /* %edx */, const CMidgardID* player);
EndTurn origEndTurn{};

bool __fastcall endTurnProbe(void* thisptr, int /* %edx */, const CMidgardID* player)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto rngBefore = rngCount();
    const auto state = turnSnapshot();
    CMidgardID id{};
    const bool idOk = readAt(player, 0, id);

    const bool result = origEndTurn(thisptr, 0, player);

    const auto rngAfter = rngCount();
    // Счётчики - только если конец хода того же игрока, чей ход ИИ начался последним.
    const bool same = idOk && state.known && state.player.value == id.value;
    countCall("ai_turn");
    emit(seq,
         fmt::format(
             R"("ev":"ai_turn","phase":"end","player":{},"turn":{},"cause":"{}","arg1":null,"arg2":null,"arg3":null,"pass_begins":{},"passes":{},"steps":{},"rng_before":{},"rng_after":{},"ret_addr":"{}","ret":{},"seq_end":{})",
             jsonId(player), same ? state.turn : notComputed,
             causeOf(endTurnSites, returnAddress), jsonInt(same, state.passBegins),
             jsonInt(same, state.passes), jsonInt(same, state.steps), rngBefore, rngAfter,
             hexAddress(returnAddress), result, nextSeq()));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ai_order - 0x5e811a (+ команда 0x41f21d)
 */

constexpr CallSite orderDirectSites[] = {
    {0x44e6cc, "strategic_orders"}, // CAiStrategic 0x44e29e: раздача приказов в начале хода
    {0x42eaba, "cmd"},              // 0x42ea8f <- команда StackChangeOrder 0x41f21d
};

constexpr CallSite orderCmdSites[] = {
    {0x4500d6, "change_order_action"}, // 0x450071 (CAiChangeOrderAction)
    {0x456198, "ai_45603b"},
    {0x4566ca, "ai_45603b"},
    {0x456bcb, "ai_456a85"},
    {0x46e22e, "operational_orders"}, // CAiOperational 0x46defb (фаза 2)
    {0x46e2d6, "operational_orders"},
};

const char* orderName(int id)
{
    switch (static_cast<OrderId>(id)) {
    case OrderId::Normal:
        return R"("normal")";
    case OrderId::Stand:
        return R"("stand")";
    case OrderId::Guard:
        return R"("guard")";
    case OrderId::AttackStack:
        return R"("attack_stack")";
    case OrderId::DefendStack:
        return R"("defend_stack")";
    case OrderId::SecureCity:
        return R"("secure_city")";
    case OrderId::Roam:
        return R"("roam")";
    case OrderId::MoveToLocation:
        return R"("move_to_location")";
    case OrderId::DefendLocation:
        return R"("defend_location")";
    case OrderId::Bezerk:
        return R"("bezerk")";
    case OrderId::Assist:
        return R"("assist")";
    case OrderId::Steal:
        return R"("steal")";
    case OrderId::DefendCity:
        return R"("defend_city")";
    default:
        return "null";
    }
}

struct StackOrders
{
    int order{notComputed};
    int aiOrder{notComputed};
};

StackOrders stackOrders(const IMidgardObjectMap* objectMap, const CMidgardID* stackId)
{
    const auto* stack = readableStack(objectMap, stackId);
    if (!stack)
        return {};
    return {static_cast<int>(stack->order.id), static_cast<int>(stack->aiOrder.id)};
}

thread_local std::uintptr_t orderCmdReturn{0};

using OrderCmd = bool(__fastcall*)(void* thisptr,
                                   int /* %edx */,
                                   std::uintptr_t a1,
                                   std::uintptr_t a2,
                                   std::uintptr_t a3,
                                   std::uintptr_t a4);
OrderCmd origOrderCmd{};

bool __fastcall orderCmdProbe(void* thisptr,
                              int /* %edx */,
                              std::uintptr_t a1,
                              std::uintptr_t a2,
                              std::uintptr_t a3,
                              std::uintptr_t a4)
{
    const auto saved = orderCmdReturn;
    orderCmdReturn = callerOf(_ReturnAddress());
    const bool result = origOrderCmd(thisptr, 0, a1, a2, a3, a4);
    orderCmdReturn = saved;
    return result;
}

using ChangeOrder = bool(__stdcall*)(const CMidgardID* stackId,
                                     const LOrderCategory* order,
                                     const CMidgardID* target,
                                     int flag,
                                     IMidgardObjectMap* objectMap,
                                     int apply);
ChangeOrder origChangeOrder{};

bool __stdcall changeOrderProbe(const CMidgardID* stackId,
                                const LOrderCategory* order,
                                const CMidgardID* target,
                                int flag,
                                IMidgardObjectMap* objectMap,
                                int apply)
{
    const auto seq = nextSeq();
    const auto direct = callerOf(_ReturnAddress());
    const bool viaCmd = direct == 0x42eaba && orderCmdReturn != 0;
    const auto returnAddress = viaCmd ? orderCmdReturn : direct;
    const auto rngBefore = rngCount();
    const auto before = stackOrders(objectMap, stackId);

    const bool result = origChangeOrder(stackId, order, target, flag, objectMap, apply);

    const auto rngAfter = rngCount();
    const auto seqEnd = nextSeq();
    const auto after = stackOrders(objectMap, stackId);
    int orderId = notComputed;
    readAt(order, 8, orderId); // LOrderCategory: vftable, table, id

    countCall("ai_order");
    emit(seq,
         fmt::format(
             R"("ev":"ai_order","stack":"{}","order":{},"order_name":{},"target":"{}","flag":{},"apply":{},"order_before":{},"order_after":{},"ai_order_before":{},"ai_order_after":{},"via":"{}","cause":"{}","rng_before":{},"rng_after":{},"ret_addr":"{}","ret":{},"seq_end":{})",
             safeId(stackId), orderId, orderName(orderId), safeId(target), (flag & 0xff) != 0,
             apply != 0, before.order, after.order, before.aiOrder, after.aiOrder,
             viaCmd ? "cmd" : "direct",
             viaCmd ? causeOf(orderCmdSites, returnAddress) : causeOf(orderDirectSites, direct),
             rngBefore, rngAfter, hexAddress(returnAddress), result, seqEnd));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ai_budget - 0x44661c
 */

constexpr std::ptrdiff_t budgetMapOffset = 0x20; // this = объект бюджета + 0x20

using BudgetInsert = void*(__fastcall*)(void* thisptr,
                                        int /* %edx */,
                                        void* out,
                                        const int* type,
                                        const void* value);
BudgetInsert origBudgetInsert{};

std::string bankAt(const void* base, std::size_t offset)
{
    Bank bank{};
    return readAt(base, offset, bank) ? bankObject(bank) : std::string{"null"};
}

void* __fastcall budgetInsertProbe(void* thisptr,
                                   int /* %edx */,
                                   void* out,
                                   const int* type,
                                   const void* value)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto rngBefore = rngCount();

    auto* result = origBudgetInsert(thisptr, 0, out, type, value);

    const auto rngAfter = rngCount();
    const auto* owner = static_cast<const char*>(thisptr) - budgetMapOffset;
    std::uint8_t flag = 0;
    readAt(value, 0x18, flag);

    countCall("ai_budget");
    emit(seq,
         fmt::format(
             R"("ev":"ai_budget","player":{},"type":{},"flag":{},"budget":{},"bank":{},"spell_bank":{},"rng_before":{},"rng_after":{},"ret_addr":"{}")",
             jsonId(owner + 4), intAt(type), flag != 0, bankAt(value, 0), bankAt(owner, 8),
             bankAt(owner, 0x14), rngBefore, rngAfter, hexAddress(returnAddress)));
    return result;
}

/* ---------------------------------------------------------------------------------------------
 * ai_chat - 0x4733a4
 */

constexpr CallSite chatSites[] = {
    {0x44eba7, "strategic"},      // CAiStrategic, действия дипломатии
    {0x466f54, "tactic"},         // CAiTactic
    {0x474b32, "trade_reply"},    // 0x474aee: ответ человека на сделку ИИ
    {0x474d3b, "alliance_reply"}, // 0x474cf1: ответ человека на союз ИИ
    {0x474dcf, "react_474d40"},   // 0x474d40 (обмен картами)
    {0x474e5d, "react_474dd2"},   // 0x474dd2
};

/** LAiMsg: номер категории -> имя (Interf LAiMsg.dbf). */
const char* chatCategoryName(int id)
{
    static const char* const names[] = {
        "PLAYER_ACCEPTS_ALL", "PLAYER_REFUSED_ALL", "AI_BREAK_ALLIANCE", "PLAYER_BREAK_ALL",
        "AI_OFFERS_ALLIANCE", "PLAYER_GIVE_GOLD",   "AI_GIVE_GOLD",      "TRADE_ACCEPTED",
        "TRADE_REFUSED",      "HATE_MESSAGE",       "NO_GOLD",           "PLAYER_GIVE_GOLD_N",
    };
    return id >= 0 && id < static_cast<int>(std::size(names)) ? names[id] : nullptr;
}

using Chat = bool(__fastcall*)(void* thisptr,
                               int /* %edx */,
                               const CMidgardID* to,
                               const void* category);
Chat origChat{};

bool __fastcall chatProbe(void* thisptr, int /* %edx */, const CMidgardID* to, const void* category)
{
    const auto seq = nextSeq();
    const auto returnAddress = callerOf(_ReturnAddress());
    const auto rngBefore = rngCount();

    const bool result = origChat(thisptr, 0, to, category);

    const auto rngAfter = rngCount();
    int categoryId = notComputed;
    readAt(category, 8, categoryId);
    const char* name = chatCategoryName(categoryId);

    countCall("ai_chat");
    emit(seq,
         fmt::format(
             R"("ev":"ai_chat","from":{},"to":{},"category":{},"category_name":{},"roll":{},"rng_before":{},"rng_after":{},"cause":"{}","ret_addr":"{}","ret":{})",
             jsonId(static_cast<const char*>(thisptr) + 8), jsonId(to), categoryId,
             name ? fmt::format(R"("{}")", name) : std::string{"null"},
             rollAt(rngBefore, rngAfter, 1), rngBefore, rngAfter,
             causeOf(chatSites, returnAddress), hexAddress(returnAddress), result));
    return result;
}

} // namespace

std::string aiTurnPlayer()
{
    return turnPlayer();
}

long long aiPass()
{
    return turnSnapshot().passes + 1;
}

} // namespace probe

namespace hooks {

void addAiHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    const GogHook entries[] = {
        {"ai_break_check",     0x448897, (void*)breakCheckProbe,     (void**)&origBreakCheck},
        {"ai_offer_check",     0x448aad, (void*)offerCheckProbe,     (void**)&origOfferCheck},
        {"ai_offer_chance",    0x448ae2, (void*)offerChanceProbe,    (void**)&origOfferChance},
        {"ai_gift_check",      0x448bda, (void*)giftCheckProbe,      (void**)&origGiftCheck},
        {"ai_answer_alliance", 0x474b37, (void*)answerAllianceProbe, (void**)&origAnswerAlliance},
        {"ai_alliance_chance", 0x474c21, (void*)allianceChanceProbe, (void**)&origAllianceChance},
        {"ai_answer_trade",    0x474841, (void*)answerTradeProbe,    (void**)&origAnswerTrade},
        {"ai_trade_chance",    0x4749c8, (void*)tradeChanceProbe,    (void**)&origTradeChance},
        {"ai_strategic_step",  0x44c825, (void*)memoSetProbe,        (void**)&origMemoSet},
        {"ai_pass",            0x44e777, (void*)passResetProbe,      (void**)&origPassReset},
        {"ai_turn_begin",      0x4719f8, (void*)turnBeginProbe,      (void**)&origTurnBegin},
        {"ai_turn_end",        0x41e229, (void*)endTurnProbe,        (void**)&origEndTurn},
        {"ai_order",           0x5e811a, (void*)changeOrderProbe,    (void**)&origChangeOrder},
        {"ai_order_cmd",       0x41f21d, (void*)orderCmdProbe,       (void**)&origOrderCmd},
        {"ai_budget",          0x44661c, (void*)budgetInsertProbe,   (void**)&origBudgetInsert},
        {"ai_chat",            0x4733a4, (void*)chatProbe,           (void**)&origChat},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
}

} // namespace hooks
