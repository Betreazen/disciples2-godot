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
#include "probeaiutil.h"
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <fmt/format.h>
#include <string>
#include <windows.h>

/*
 * Схемы 6.5 и 7.5: строка ai_rng - бросок R250 (generateRandomNumber 0x54851f) или CRT
 * (generateRandomNumberStd 0x548576) из решения игры вне показа: ИИ (тактика, стратегия,
 * дипломатия, вор, места), события, эффекты карты.
 *
 * Места - по адресу возврата из обёртки генератора. Список - ВСЕ прямые вызовы обеих обёрток в
 * Discipl2.exe (GOG 3.01, поиск E8 rel32 по .text, 18.10.2026), кроме показа: грунт
 * CIsoEngineGround 0x5423fb (четыре R250 на клетку - около 1,4 млн бросков за партию), туман,
 * облака, анимации, звук, экран боя, и кроме боя (атаки LAttackClass, CResultSender, CBatUnitAnim -
 * их броски уже пишут строки rng / rng_std боя). Без этих строк значения бросков ИИ не
 * восстановить: счётчик R250 общий, и грунт с потока показа вклинивается между бросками сервера.
 *
 * У перемешиваний и общих выборов (одна функция, несколько вызывающих) поле outer - цепочка
 * адресов возврата из обёрток выше по стеку: стек просматривается вверх от адреса возврата из
 * генератора (не дальше stackScanWords слов на уровень и не выше StackBase потока), годен адрес
 * в коде exe, перед которым стоит E8 rel32 с целью - началом обёртки. Следующий уровень ищется,
 * только если найденный адрес лежит внутри обёртки следующего уровня. origin - имя по самому
 * глубокому известному адресу цепочки.
 */

namespace probe {

namespace {

using namespace aiutil;

struct Range
{
    std::uintptr_t begin;
    std::uintptr_t end;
};

/** Место броска: адрес возврата из генератора, имя, обёртки вверх по стеку (begin 0 - нет). */
struct RngSite
{
    std::uintptr_t returnAddress;
    const char* name;
    Range wrappers[3];
};

// clang-format off
constexpr RngSite rngSites[] = {
    // R250 (0x54851f).
    {0x41de5b, "riot_days",          {}},                       // 0x41dda5 результат знатного: rng(n) + 1
    {0x434b7e, "capital_spells",     {}},                       // 0x434b3b взятая столица: rng(100) < GAIN_SPELL
    {0x435f7a, "terrain_shuffle",    {{0x435f57, 0x435fa1}, {0x435d28, 0x435e06}}}, // смена земли/грунта
    {0x4394c0, "shuffle_misfit",     {{0x43949e, 0x4394d6}, {0x470abb, 0x470b99}, {0x46e92c, 0x46ed41}}},
    {0x441ea0, "event_chance",       {}},                       // 0x441d9f CheckEventConditions
    {0x4430a5, "thief_dice1",        {}},                       // 0x443067 треугольный бросок вора
    {0x4430ae, "thief_dice2",        {}},
    {0x444432, "spy_discover",       {}},                       // 0x44430f rng(100) < SPY_DISCOV
    {0x4489c3, "dipl_break",         {}},                       // 0x448897 разрыв союза
    {0x448ad5, "dipl_offer",         {}},                       // 0x448aad предложение союза
    {0x448bfd, "dipl_gift_kind",     {}},                       // 0x448bda вид подарка
    {0x448c08, "dipl_gift_kind2",    {}},
    {0x449572, "gift_item_shuffle",  {}},                       // 0x449550 пары «крепость, предмет»
    {0x4495fa, "shuffle_list",       {{0x4495d8, 0x449610}, {0x46a9f5, 0x46aad3}}},
    {0x44aee6, "leader_pick",        {}},                       // 0x44ad0f list[R250(count)]
    {0x459b3e, "mercs_shuffle",      {}},                       // 0x459b1c лагерь наёмников
    {0x45bf7f, "noble_item",         {}},                       // 0x45bf2f кража предмета
    {0x45c7e8, "noble_shuffle",      {}},                       // 0x45c7c3 порядок действий знатного
    {0x46b805, "merchant_item",      {}},                       // 0x46b6f8 экземпляр склада торговца
    {0x46e96f, "op_spell_start",     {{0x46e92c, 0x46ed41}}},   // 0x46e92c запись круга категорий
    {0x46ebbf, "op_spell_count",     {{0x46e92c, 0x46ed41}}},   // n = min(rng(число) + 1, всего)
    {0x46fe84, "harass_chance",      {{0x46fe5e, 0x4703d1}}},   // 0x46fe5e rng(100) <= 49
    {0x46feaa, "harass_count",       {{0x46fe5e, 0x4703d1}}},   // rng(4) + 2
    {0x472350, "attack_spell_kind",  {}},                       // 0x472142 категория перед атакой (0x47234b)
    {0x472415, "attack_spell_pick",  {}},                       // одно из старших (0x472410)
    {0x47341b, "ai_chat_msg",        {}},                       // 0x4733a4 строка TAiMsg
    {0x474979, "answer_trade",       {}},                       // 0x474841 rng(100) < TradeChance
    {0x474bcf, "answer_alliance",    {}},                       // 0x474b37 rng(100) < шанс
    {0x474d5b, "answer_map",         {}},                       // 0x474d40 rng(100) < 30
    {0x5efd32, "mountain",           {}},                       // 0x5efd06 CEffectMountain
};

// CRT (0x548576): rand() % n.
constexpr RngSite crtSites[] = {
    {0x41e100, "riot_crt",           {}},                       // 0x41e0a5 CNobleActionResultRiot
    {0x42afa0, "new_game_desc",      {}},                       // 0x42ae90 описание человека новой партии
    {0x44b12b, "leader_name",        {}},                       // 0x44b09f имя лидера, косметика
    {0x44c13c, "unit_pick",          {}},                       // 0x44c06a list[rand() % count]
    {0x5e6002, "ground_variant",     {}},                       // 0x5e5f84 вариант грунта CVisitorMapGround
};

/** Имя по адресу возврата из обёртки (самый глубокий известный адрес цепочки outer). */
struct Origin
{
    std::uintptr_t outer;
    const char* name;
};

constexpr Origin origins[] = {
    {0x435c28, "change_ground"},   // CEffectChangeGround 0x4359a7
    {0x43f308, "change_terrain"},  // CEffectChangeTerrain 0x43f128
    {0x439336, "misfit"},          // CEffectMisfit 0x4392e2: позиции подложного приказа
    {0x470b49, "op_spell"},        // 0x470abb из выбора 0x46e92c
    {0x448f08, "gift_spell"},      // 0x448e84 подарок-заклинание
    {0x46a8e2, "mage"},            // CAiVisitMageAction 0x46a89e
    {0x46de76, "op_spell_part2"},  // CAiOperational 0x46d691
    {0x47008f, "harass_targets"},  // 0x46fe5e цели
    {0x4702f3, "harass_spells"},   // 0x46fe5e заклинания
    {0x627523, "battle"},          // CResultSender 0x62738b (в бою; на случай вызова вне показа боя)
    {0x46fe53, "harass_far"},      // 0x46fe35 первый проход
    {0x46fe5c, "harass_near"},     // второй
    {0x46e8fd, "op_spell_0x46e881"},
    {0x46eb9a, "op_spell_fallback"}, // запасной заход 0x46e92c в себя
    {0x46f162, "op_spell_strike"},   // удар 0x46f08c
    {0x46fe09, "op_spell_0x46fd8d"},
};
// clang-format on

constexpr std::size_t stackScanWords = 512;

template <std::size_t N>
const RngSite* findSite(const RngSite (&sites)[N], std::uintptr_t returnAddress)
{
    for (const auto& site : sites) {
        if (site.returnAddress == returnAddress)
            return &site;
    }
    return nullptr;
}

/** Адрес value - возврат из call rel32 в target: E8 по value - 5 внутри кода exe. */
bool returnsFrom(std::uintptr_t value, std::uintptr_t target)
{
    if (value < codeBegin + 5 || value >= codeEnd)
        return false;
    const auto* call = reinterpret_cast<const unsigned char*>(value - 5);
    if (call[0] != 0xe8)
        return false;
    std::int32_t rel = 0;
    std::memcpy(&rel, call + 1, sizeof(rel));
    return value + static_cast<std::uintptr_t>(rel) == target;
}

struct Chain
{
    std::uintptr_t outer[3]{};
    std::size_t length{};
};

/** Цепочка адресов возврата из обёрток места (заголовок файла). */
Chain outerChain(const RngSite& site, const void* returnSlot)
{
    Chain chain;
    if (!returnSlot || site.wrappers[0].begin == 0)
        return chain;

    const auto* tib = reinterpret_cast<const NT_TIB*>(NtCurrentTeb());
    const auto* stackTop = static_cast<const std::uintptr_t*>(tib->StackBase);
    const auto* slot = static_cast<const std::uintptr_t*>(returnSlot) + 1;
    for (const auto& wrapper : site.wrappers) {
        if (wrapper.begin == 0)
            break;
        if (chain.length > 0) {
            const auto previous = chain.outer[chain.length - 1];
            if (previous < wrapper.begin || previous >= wrapper.end)
                break;
        }
        bool found = false;
        for (std::size_t i = 0; i < stackScanWords && slot < stackTop; ++i, ++slot) {
            if (returnsFrom(*slot, wrapper.begin)) {
                chain.outer[chain.length++] = *slot;
                ++slot;
                found = true;
                break;
            }
        }
        if (!found)
            break;
    }
    return chain;
}

std::string originOf(const Chain& chain)
{
    for (std::size_t i = chain.length; i-- > 0;) {
        for (const auto& origin : origins) {
            if (origin.outer == chain.outer[i])
                return fmt::format(R"("{}")", origin.name);
        }
    }
    return {"null"};
}

std::string chainJson(const Chain& chain)
{
    if (chain.length == 0)
        return {"null"};
    std::string out{"["};
    for (std::size_t i = 0; i < chain.length; ++i)
        out += (i ? "," : "") + hexOf(chain.outer[i]);
    return out + ']';
}

} // namespace

void tagAiRng(std::uint64_t seq,
              std::uintptr_t returnAddress,
              const void* returnSlot,
              bool isStd,
              unsigned int maxValue,
              int result)
{
    const auto* site = isStd ? findSite(crtSites, returnAddress) : findSite(rngSites, returnAddress);
    if (!site)
        return;

    // Сырое значение rand - только если crt_rand стоял и позвался внутри этого же вызова.
    int raw = 0;
    const bool rawKnown = isStd && rawRandAfter(seq, raw);
    const auto chain = outerChain(*site, returnSlot);

    countCall("ai_rng");
    emit(seq,
         fmt::format(
             R"("ev":"ai_rng","site":"{}","kind":"{}","n":{},"max":{},"r":{},"raw":{},"battle":{},"ret_addr":"{}","player":{},"outer":{},"origin":{})",
             site->name, isStd ? "crt" : "r250", isStd ? rngStdCount() : rngCount(), maxValue,
             result, rawKnown ? std::to_string(raw) : std::string{"null"}, battleActive(),
             hexAddress(returnAddress), aiTurnPlayer(), chainJson(chain), originOf(chain)));
}

} // namespace probe
