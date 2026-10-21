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
#include "gamesettings.h"
#include "midgard.h"
#include "midgardid.h"
#include "midgardobjectmap.h"
#include "midstack.h"
#include <cstdint>
#include <cstring>
#include <fmt/format.h>
#include <intrin.h>
#include <string>
#include <vector>
#include <Windows.h>

/*
 * Схема корпуса 6.2: состояние генератора боя (rng_seed) и запас хода отрядов (ap_restore).
 *
 * Адреса - game-dev/Discipl2.exe (Gog 3.01, ImageBase 0x400000), capstone. Хуки только читают:
 * оригинал зовётся с теми же аргументами, его ответ возвращается без изменений.
 *
 * rng_seed. Состояние R250 - индекс 0x838c24 и 250 слов 0x7b4978. Пишут их в образе ровно три места
 * (все ссылки на оба адреса в .text): шаг 0x67819c, его инлайновая копия в 0x6781dd и
 * setState 0x67816c (index % 250, копия 250 слов). setState зовёт только 0x548473 - та же функция
 * под замком 0x836fd8 (CRITICAL_SECTION, InitializeCriticalSection в статическом конструкторе
 * 0x548409), __stdcall(index, table), ret 8. Её зовут:
 *   - 0x5484bd reseed(seed), ret 4: getState 0x548429 в локальный буфер и setState(seed, буфер) -
 *     меняется только индекс (возврат 0x548505);
 *   - 0x429de7 в 0x429d3b (открытие сценария на сервере): загрузка - setState(scenario+0x669,
 *     scenario+0x66d), таблица из сохранения.
 * reseed зовут 0x429dc8 (там же, новая игра: reseed(timeGetTime())) и 0x61b8c4 (0x61b89c <- 0x40224f:
 * reseed(таймер CUIManager 0x5613e1)). Хук стоит на 0x548473 - точку схождения всех записей вне
 * шага; хук reseed только запоминает свой адрес возврата, чтобы назвать внешнюю причину.
 *
 * rng_state (схема 7.1, часть 3). Почему загрузка не пишет rng_seed: 0x429d3b(файл, ..., новая,
 * пересев) пересевает при (новая && пересев) и зовёт setState из сохранения только при (!новая &&
 * !пересев) - 0x429db7..0x429de7. Флаг «пересев» кладут 0x403479 (новая игра) и 0x403540
 * (загрузка) через CMidServerBuilderFull 0x4335d0 (+9) и 0x422b38 - это 0x61a642 =
 * GameSettings::random (+0x155, Disciple.ini «Random»); тем же флагом 0x40224f решает пересев от
 * таймера при старте. При Random = 1 загрузка генератор НЕ трогает: он идёт дальше из состояния
 * процесса, а записанное в сохранении (заголовок 0xb4d байт, [[server+4]+0x28]: индекс +0x669,
 * 250 слов +0x66d - их же пишет getState 0x42a017 при записи) пропадает. Ранг 1. Поэтому
 * noteScenarioOpen (хук game_start 0x429603, до оригинала - после пересева/восстановления) пишет
 * снимок состояния, с которым открывается сценарий, и сравнение с заголовком сохранения.
 *
 * ap_restore. Байт CMidStack::movement (+0x98) пишут две инструкции: 0x5ebfbe (CMidStack::initialize,
 * создание отряда) и 0x5ecfd5 (CVisitorChangeStackMoveAllowance::apply 0x5ecf76). Визитёр строит
 * только changeStackMove 0x5e780d, __stdcall(stackId*, amount, map, apply), ret 0x10:
 * movement = clamp(movement - amount, 0, MOVE лидера). Начало хода - CEffectRegenUnits::apply
 * 0x43b873 -> 0x5d41d8(map, playerId): каждому отряду владельца changeStackMove(id,
 * movement - 0x5ed035(stack, map), map, 1) (0x5d4452..0x5d446b). Хук стоит на 0x5e780d и пишет
 * строку на каждый вызов; причина - по адресу возврата (все одиннадцать вызовов в .text).
 */

namespace probe {

namespace {

using namespace game;

constexpr int notComputed = -1;

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

std::uintptr_t returnAddressOf(void* address)
{
    return reinterpret_cast<std::uintptr_t>(address);
}

/* ---------------------------------------------------------------------------------------------
 * rng_seed - setState 0x548473 (+ reseed 0x5484bd)
 */

constexpr std::uintptr_t rngIndexAddress = 0x838c24;
constexpr std::uintptr_t rngTableAddress = 0x7b4978;
constexpr std::size_t rngTableSize = 250;
constexpr std::uintptr_t rngLockAddress = 0x836fd8;

/** Адрес возврата из вызова setState внутри reseed (call 0x548500). */
constexpr std::uintptr_t reseedSetStateReturn = 0x548505;

/** Внешний адрес возврата (у reseed - адрес возврата самого reseed). */
constexpr CallSite seedSites[] = {
    {0x429dcd, "new_game_time"},   // 0x429d3b: новая игра, reseed(timeGetTime())
    {0x429dec, "load_save"},       // 0x429d3b: загрузка, setState из объекта сценария
    {0x61b8c9, "reseed_ui_timer"}, // 0x61b89c <- 0x40224f: reseed(таймер CUIManager)
};

using SetState = void(__stdcall*)(std::uint32_t index, const std::uint32_t* table);
using Reseed = void(__stdcall*)(std::uint32_t seed);
SetState origSetState{};
Reseed origReseed{};

/** Адрес возврата reseed, пока он выполняется; 0 - вне reseed. */
thread_local std::uintptr_t reseedReturn{0};

void __stdcall reseedProbe(std::uint32_t seed)
{
    const auto saved = reseedReturn;
    reseedReturn = returnAddressOf(_ReturnAddress());
    origReseed(seed);
    reseedReturn = saved;
}

std::string tableHex(const std::uint32_t* words, int& wide)
{
    std::string hex;
    hex.reserve(rngTableSize * 4);
    wide = 0;
    for (std::size_t i = 0; i < rngTableSize; ++i) {
        if (words[i] > 0xffff)
            ++wide;
        hex += fmt::format("{:04x}", words[i] & 0xffff);
    }

    return hex;
}

/** Критическая секция движка на время вызова: RAII, чтобы исключение из оригинала её не оставило. */
class EngineLock
{
public:
    explicit EngineLock(LPCRITICAL_SECTION section)
        : section{section}
    {
        EnterCriticalSection(section);
    }
    ~EngineLock()
    {
        LeaveCriticalSection(section);
    }
    EngineLock(const EngineLock&) = delete;
    EngineLock& operator=(const EngineLock&) = delete;

private:
    LPCRITICAL_SECTION section;
};

const char* seedCause(bool viaReseed, std::uintptr_t outer)
{
    // Хук rng_reseed выключен через hooks.txt: внешнего адреса нет, но путь через reseed известен.
    if (viaReseed && outer == reseedSetStateReturn)
        return "reseed_unhooked";
    return causeOf(seedSites, outer);
}

void __stdcall setStateProbe(std::uint32_t index, const std::uint32_t* table)
{
    const auto seq = nextSeq();
    const auto returnAddress = returnAddressOf(_ReturnAddress());
    const bool viaReseed = returnAddress == reseedSetStateReturn;
    const auto outer = viaReseed && reseedReturn ? reseedReturn : returnAddress;

    // Замок генератора, рекурсивный: оригинал берёт его ещё раз. Снимок до и после - под одним
    // замком: шаг генератора ходит только через 0x54851f, а тот берёт эту же секцию (0x54854a),
    // так что бросок из другого потока не вклинится. Журнал пишется уже без неё.
    const auto* indexPtr = reinterpret_cast<const std::uint32_t*>(rngIndexAddress);
    std::uint32_t words[rngTableSize];
    std::uint32_t indexBefore = 0;
    std::uint32_t indexAfter = 0;
    {
        const EngineLock lock{reinterpret_cast<LPCRITICAL_SECTION>(rngLockAddress)};
        indexBefore = *indexPtr;
        origSetState(index, table);
        indexAfter = *indexPtr;
        std::memcpy(words, reinterpret_cast<const void*>(rngTableAddress), sizeof(words));
    }

    int wide = 0;
    const auto hex = tableHex(words, wide);
    countCall("rng_seed");
    emit(seq,
         fmt::format(
             R"("ev":"rng_seed","n":{},"arg":{},"index_before":{},"index_after":{},"via":"{}","cause":"{}","ret_addr":"{}","wide":{},"table":"{}")",
             rngCount(), index, indexBefore, indexAfter, viaReseed ? "reseed" : "set_state",
             seedCause(viaReseed, outer), hexAddress(outer), wide, hex));
}

/* ---------------------------------------------------------------------------------------------
 * rng_state - открытие сценария 0x429603 (зовёт хук game_start, probeui.cpp)
 */

/** Заголовок сценария на сервере: [[server+4]+0x28], 0xb4d байт (0x429d5a). */
constexpr std::size_t serverDataOffset = 4;
constexpr std::size_t scenarioHeaderOffset = 0x28;
constexpr std::size_t savedIndexOffset = 0x669;
constexpr std::size_t savedTableOffset = 0x66d;

struct SavedRng
{
    bool present{};
    std::uint32_t index{};
    std::uint32_t words[rngTableSize]{};
};

/**
 * Состояние R250 из заголовка сохранения; present = false - заголовка нет: новая игра (0x429d3b
 * освобождает его до открытия, 0x429dec..0x429e00) или путь 0x429c92 (обнуляет всегда).
 */
SavedRng savedRngOf(const void* server)
{
    SavedRng saved{};
    std::uintptr_t data{};
    std::uintptr_t header{};
    if (!readAt(server, serverDataOffset, data)
        || !readAt(reinterpret_cast<const void*>(data), scenarioHeaderOffset, header) || !header)
        return saved;

    const auto* p = reinterpret_cast<const char*>(header);
    if (!readAt(p, savedIndexOffset, saved.index) || !readable(p + savedTableOffset, sizeof(saved.words)))
        return saved;

    std::memcpy(saved.words, p + savedTableOffset, sizeof(saved.words));
    saved.present = true;
    return saved;
}

/** GameSettings::random (+0x155, 0x61a642) как JSON: true, false или null. */
std::string randomSetting()
{
    const auto* midgard = CMidgardApi::get().instance();
    if (!midgard || !midgard->data || !midgard->data->settings || !*midgard->data->settings)
        return {"null"};

    return (*midgard->data->settings)->random ? "true" : "false";
}

} // namespace

void noteScenarioOpen(const void* server, bool newGame, std::uintptr_t returnAddress)
{
    const auto seq = nextSeq();
    const auto saved = savedRngOf(server);

    std::uint32_t words[rngTableSize];
    std::uint32_t index = 0;
    std::uint64_t n = 0;
    {
        const EngineLock lock{reinterpret_cast<LPCRITICAL_SECTION>(rngLockAddress)};
        index = *reinterpret_cast<const std::uint32_t*>(rngIndexAddress);
        std::memcpy(words, reinterpret_cast<const void*>(rngTableAddress), sizeof(words));
        n = rngCount();
    }

    // setState кладёт index % 250 и копирует 250 слов целиком (0x67816c).
    const bool same = saved.present && index == saved.index % rngTableSize
                      && std::memcmp(words, saved.words, sizeof(words)) == 0;
    int wide = 0;
    const auto hex = tableHex(words, wide);
    countCall("rng_state");
    emit(seq,
         fmt::format(
             R"("ev":"rng_state","what":"{}","n":{},"index":{},"wide":{},"table":"{}","saved_index":{},"saved_same":{},"random":{},"ret_addr":"{}")",
             newGame ? "new" : "load", n, index, wide, hex,
             saved.present ? std::to_string(saved.index) : std::string{"null"},
             saved.present ? (same ? "true" : "false") : "null", randomSetting(),
             hexAddress(returnAddress)));
}

namespace {

/* ---------------------------------------------------------------------------------------------
 * ap_restore - changeStackMove 0x5e780d
 */

/** Все одиннадцать вызовов 0x5e780d в .text (E8), адрес возврата -> причина. */
constexpr CallSite moveSites[] = {
    {0x5d4470, "turn_start"},     // CEffectRegenUnits::apply 0x43b873 -> 0x5d41d8: move = max
    {0x43590c, "ap_effect"},      // CEffectChangeAP::apply 0x4358d4 (снаружи строка ap_apply)
    {0x439fc8, "stack_move"},     // CEffectMove::apply 0x439e84: цена пути
    {0x437681, "rod_place"},      // CEffectDropRod::apply 0x437606
    {0x5d9ffb, "rod_break"},      // 0x5d9fab <- CEffectBreakRod::apply 0x433c65
    {0x4398b4, "modifier_clamp"}, // CEffectModifier::apply 0x439799: move > max -> max
    {0x5d6e1a, "clamp_max"},      // 0x5d6db7 <- 0x5d6cfb: move > max -> max
    {0x5d6ee7, "skill_pick"},     // 0x5d6e21, выбор навыка лидера: move = max
    {0x42f0f0, "game_start"},     // 0x42f036 <- 0x4228e5 (после открытия сценария): move = max
    {0x43089c, "stacks_init"},    // 0x430798 <- 0x43002c: всем отрядам move = max
    {0x42eb44, "zero_9999"},      // 0x42eb1d <- 0x41e507 <- 0x45149a: amount 9999, не опознан
};

using ChangeStackMove = bool(__stdcall*)(const CMidgardID* stackId,
                                         int amount,
                                         IMidgardObjectMap* objectMap,
                                         int apply);
ChangeStackMove origChangeStackMove{};

/** 0x5ed035, __thiscall(stack, map), ret 4: MOVE лидера с модификаторами, 0 - лидера нет. */
using StackMaxMove = int(__fastcall*)(const CMidStack* stack, int edx, const IMidgardObjectMap* map);
const auto stackMaxMove = reinterpret_cast<StackMaxMove>(0x5ed035);

const char* zeroReason(int moveAfter, int max, bool leaderFound)
{
    if (moveAfter != 0)
        return nullptr;
    if (max < 0)
        return "unknown";
    if (max == 0)
        return leaderFound ? "max0" : "max0_no_leader";
    return "spent";
}

std::string jsonString(const char* value)
{
    return value ? fmt::format(R"("{}")", value) : std::string{"null"};
}

bool __stdcall changeStackMoveProbe(const CMidgardID* stackId,
                                    int amount,
                                    IMidgardObjectMap* objectMap,
                                    int apply)
{
    const auto seq = nextSeq();
    const auto returnAddress = returnAddressOf(_ReturnAddress());
    const auto* before = readableStack(objectMap, stackId);
    const int moveBefore = before ? static_cast<int>(before->movement) : notComputed;

    const bool result = origChangeStackMove(stackId, amount, objectMap, apply);

    const auto seqEnd = nextSeq();
    CMidgardID id{};
    const bool idOk = readAt(stackId, 0, id);
    // Движок в самом apply считает тот же максимум (0x5ecf9f..0x5ecfc4), так что вызов 0x5ed035
    // здесь не рискованнее оригинала.
    const auto* stack = readableStack(objectMap, stackId);
    const int moveAfter = stack ? static_cast<int>(stack->movement) : notComputed;
    const int max = stack ? stackMaxMove(stack, 0, objectMap) : notComputed;
    const bool leaderFound = stack && readableUnit(objectMap, &stack->leaderId) != nullptr;

    countCall("ap_restore");
    emit(seq,
         fmt::format(
             R"("ev":"ap_restore","stack":"{}","player":"{}","amount":{},"move_before":{},"move_after":{},"max":{},"leader":"{}","leader_found":{},"leader_alive":{},"zero":{},"cause":"{}","ret_addr":"{}","ret":{},"seq_end":{})",
             idOk ? idToString(&id) : std::string{"null"},
             stack ? idToString(&stack->ownerId) : std::string{"null"}, amount, moveBefore,
             moveAfter, max, stack ? idToString(&stack->leaderId) : std::string{"null"},
             leaderFound, stack ? (stack->leaderAlive ? "true" : "false") : "null",
             jsonString(stack ? zeroReason(moveAfter, max, leaderFound) : nullptr),
             causeOf(moveSites, returnAddress), hexAddress(returnAddress), result, seqEnd));
    return result;
}

} // namespace

} // namespace probe

namespace hooks {

void addStateHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    const GogHook entries[] = {
        {"rng_seed",   0x548473, (void*)setStateProbe,        (void**)&origSetState},
        {"rng_reseed", 0x5484bd, (void*)reseedProbe,          (void**)&origReseed},
        {"ap_restore", 0x5e780d, (void*)changeStackMoveProbe, (void**)&origChangeStackMove},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
}

} // namespace hooks
