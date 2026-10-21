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

#include "midgardid.h"
#include "probe.h"
#include "utils.h"
#include "version.h"
#include <cstdint>
#include <fmt/format.h>
#include <map>
#include <mutex>
#include <string>
#include <vector>
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

/*
 * Схема 7.6: портреты панели боя (CBatUnitGroup2) - вспышки, состояния и надписи (docs/probe-av.md §8,
 * разбор capstone, ранг 1, если не сказано иное). Строка на событие, не на кадр.
 *
 * Запись портрета панели (узел карты «юнит -> запись», 0x655e70): +0x0c большой (150 точек, иначе 70),
 * +0x18 картинка лица (vtbl+0x24 - поставить состояние, +0x28 - прочитать), +0x20 состояние до вспышки,
 * +0x24 тиков вспышки (-1 - нет), +0x28 «держать после конца», +0x34 картинка надписи, +0x3c тиков
 * надписи, +0x40 стойкое состояние (0x1e - нет). Данные группы D=[группа+8]: +0x40 счётчик анимаций шага
 * (CAnimCounter), +0x44 тиков вспышки и надписи по скорости боя (15/10/5, 0x64a4cd), +0x50 группа показана.
 *
 * bat_flash - вспышка 0x64cee8 __thiscall(группа, CMidgardID* юнит, int* состояние, bool держать) ret 0xc:
 * состояния 7, 8, 10 (паралич, окаменение, павший; 0x64d040) ставятся сразу, если 0x64d05f(текущее, новое) разрешает
 * (mode set, before - текущее; запрещено - mode none: 8 -> 7/10, 7 -> 10, 10 -> любое, из прочих - нет); прочие - на +0x44 тиков (mode flash): прежняя вспышка снимается (0x64c24c, how=cut у
 * bat_flash_end), +0x20 = текущее, +0x24 = тиков, +0x28 = держать, счётчик шага +1. Источник по возврату:
 * 0x6371ab - ходящий (0x6370e5: пропуск, ожидание, защита, тики яда и т.п.), 0x63bfad - итог по цели
 * (0x63bcc2: класс атаки -> состояние, промах 3, иммунитет/оберег 4, урон 2, лечение 5, ...), 0x63c368 -
 * лечение переливом (9), 0x64d60e - опыт выросшего юнита (11). Стойкое состояние 0x64d2c3
 * __thiscall(группа, CMidgardID*, int*) ret 8 - строка bat_flash mode=set: 0x64d5f5 - выросший (1),
 * 0x64d63c - получивший опыт (11, жёлтый) - оба из итога опыта 0x64d4a4 (шаг 15, конец боя).
 * Цвет состояния - 0x5b18ef: есть ли (0x5b19fa), какой (таблица 0x5b191f, COLORREF 0x00BBGGRR) -> "#RRGGBB";
 * без цвета - null. Красная доля ран по состоянию - 0x5b1a63/0x5b1a91 (не пишется).
 *
 * bat_flash_end - конец вспышки: тик панели 0x64d356 зовёт 0x64d467 __stdcall(счётчик, int* тики) ret 8
 * (al = кончилась; возврат 0x64d3da - вспышка, 0x64d3bf - надпись); после конца портрет берёт +0x20
 * (если +0x40 == 0x1e) или +0x40, при «держать» - остаётся. Юнит ушёл из панели (0x64b604, 0x64c24c с
 * возвратом 0x64b7a6/0x64b7c4) - how=gone. Строка надписи - bat_text_end (те же how).
 *
 * bat_text - надпись 0x64d09c __thiscall(группа, CMidgardID*, char*) ret 8 (было в 7.3, probebat.cpp) и
 * стойкая надпись опыта 0x64d23e (тот же вид, без срока: «Опыт: +N» X008TA0005) - src=xp, persist. Картинка
 * текста - 0x64d160 __stdcall(bool большой, char*) ret 8: CImage2Text w x h = [0x6ebed8] 150 или
 * [0x6ebedc] 70 на [0x6ebee0] 85, перед текстом стиль X008TA0028; переносит по словам в ширину w (правило
 * - docs/ui-windows.md, Ин37). Поля 7.6: big, w, h, on (группа показана), found (юнит в группе есть; опыт конца боя
 * 0x64d4a4 зовётся на все четыре группы - 0x636f2d..0x636fb1).
 */

namespace probe {

namespace {

std::mutex flashMutex;

/** Цвет состояния портрета: 0x5b18ef - есть ли цвет (0x5b19fa) и какой (0x5b191f, COLORREF 0x00BBGGRR);
 *  0xffffffff - без цвета (0, 1, 4, 9, 12, 15, 24-26). */
constexpr std::uint32_t none = 0xffffffff;
constexpr std::uint32_t stateColors[30] = {
    none,     none,     0xff,     0xffff,   none,     0xff0000, 0xff00,   0x808080, 0x808080, none,
    0x0,      0x40ffff, none,     0x0,      0xff00ff, none,     0x8080,   0x800000, 0x80c0,   0xff00,
    0xf9da5e, 0xff0000, 0xff0000, 0xff00ff, none,     none,     none,     0x0,      0x0,      0x0};

std::string colorOf(int state)
{
    if (state < 0 || state >= 30 || stateColors[state] == none)
        return "null";
    const auto c = stateColors[state];
    return fmt::format(R"("#{:02X}{:02X}{:02X}")", c & 0xff, (c >> 8) & 0xff, (c >> 16) & 0xff);
}

struct Shown
{
    std::string unit;
    int state;
    std::uint64_t startMs;
};

/** Идущие вспышки и надписи по записи портрета. */
std::map<const void*, Shown> flashes;
std::map<const void*, Shown> notes;

/** Запись портрета, которую нашёл 0x64c24c внутри текущей вспышки или надписи, и была ли там прежняя. */
thread_local const char* capturedRecord{};
thread_local bool capturedCut{};
/** «Большой» из 0x64d160 внутри текущей надписи (-1 - не звали). */
thread_local int capturedBig{-1};
/** Переход «сразу» (7, 8, 10) внутри текущей вспышки: 0x64d05f(текущее, новое) - разрешён ли (-1 - не звали) и текущее. */
thread_local int capturedAllow{-1};
thread_local int capturedCurrent{-1};

const char* recordOf(const int* ticks, std::size_t offset)
{
    return reinterpret_cast<const char*>(ticks) - offset;
}

bool groupShown(const void* group)
{
    const char* data = nullptr;
    std::uint8_t on = 0;
    return readAt(group, 8, data) && data && readAt(data, 0x50, on) && on != 0;
}

int groupTicks(const void* group)
{
    const char* data = nullptr;
    int ticks = -1;
    if (readAt(group, 8, data) && data)
        readAt(data, 0x44, ticks);
    return ticks;
}

/** Конец вспышки или надписи записи 'record': строка и снятие из 'shown'. */
void ended(std::map<const void*, Shown>& shown, const char* ev, const char* record, const char* how,
           const std::string& after)
{
    Shown entry;
    {
        std::lock_guard<std::mutex> lock{flashMutex};
        const auto it = shown.find(record);
        if (it == shown.end())
            return;
        entry = it->second;
        shown.erase(it);
    }
    countCall(ev);
    emit(nextSeq(), fmt::format(R"("ev":"{}","unit":"{}","state":{},"how":"{}","ms":{}{})", ev, entry.unit,
                                entry.state, how, journalMs() - entry.startMs, after));
}

using Release = bool(__stdcall*)(void* counter, int* ticks);
Release origRelease{};

/** 0x64c24c: снять счётчик шага у идущей вспышки или надписи (тики >= 0) - здесь только узнать запись. */
bool __stdcall releaseProbe(void* counter, int* ticks)
{
    const bool released = origRelease(counter, ticks);
    switch (reinterpret_cast<std::uintptr_t>(_ReturnAddress())) {
    case 0x64cfe3: // новая вспышка (0x64cee8)
        capturedRecord = recordOf(ticks, 0x24);
        capturedCut = released;
        break;
    case 0x64d10f: // новая надпись (0x64d09c)
        capturedRecord = recordOf(ticks, 0x3c);
        capturedCut = released;
        break;
    case 0x64b7a6: // юнит ушёл из панели (0x64b604)
        if (released)
            ended(flashes, "bat_flash_end", recordOf(ticks, 0x24), "gone", {});
        break;
    case 0x64b7c4:
        if (released)
            ended(notes, "bat_text_end", recordOf(ticks, 0x3c), "gone", {});
        break;
    default:
        break;
    }
    return released;
}

using Tick = bool(__stdcall*)(void* counter, int* ticks);
Tick origTick{};

/** 0x64d467: тик вспышки или надписи; al = 1 - кончилась. */
bool __stdcall tickProbe(void* counter, int* ticks)
{
    const bool over = origTick(counter, ticks);
    if (!over)
        return over;
    const auto ret = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    if (ret == 0x64d3da) {
        // Что портрет покажет дальше (0x64d3e3): держать - текущее, иначе +0x20 при +0x40 == 0x1e, иначе +0x40.
        const char* record = recordOf(ticks, 0x24);
        std::uint8_t hold = 0;
        int before = -1;
        int steady = -1;
        readAt(record, 0x28, hold);
        readAt(record, 0x20, before);
        readAt(record, 0x40, steady);
        const auto after = hold ? std::string{R"(,"after":null,"hold":true)"}
                                : fmt::format(R"(,"after":{},"hold":false)", steady == 0x1e ? before : steady);
        ended(flashes, "bat_flash_end", record, "end", after);
    } else if (ret == 0x64d3bf) {
        ended(notes, "bat_text_end", recordOf(ticks, 0x3c), "end", {});
    }
    return over;
}

const char* flashSource(std::uintptr_t ret)
{
    switch (ret) {
    case 0x6371ab:
        return "actor";
    case 0x63bfad:
        return "target";
    case 0x63c368:
        return "heal";
    case 0x64d60e:
        return "xp_up";
    default:
        return "other";
    }
}

using Allow = int(__stdcall*)(int current, int next);
Allow origAllow{};

/** 0x64d05f: можно ли поставить 7, 8, 10 «сразу» поверх текущего (8 -> 7/10, 7 -> 10, 10 -> любое; из прочих - нет).
 *  Внутри вспышки (возврат 0x64cf89) - запомнить ответ: без него строка писала mode=set и там, где портрет не менялся
 *  (окаменение живого: 0 -> 8 запрещено; журнал 20261020-faces, Зс110). */
int __stdcall allowProbe(int current, int next)
{
    // Единственный вызывающий - 0x64cf84 (дальше and eax, 0xff); ответ в al, eax возвращается как есть.
    const auto ret = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    const int result = origAllow(current, next);
    if (ret == 0x64cf89) {
        capturedAllow = (result & 0xff) != 0 ? 1 : 0;
        capturedCurrent = current;
    }
    return result;
}

using Flash = void(__fastcall*)(void* group, int edx, const game::CMidgardID* unit, int* state, bool hold);
Flash origFlash{};

void __fastcall flashProbe(void* group, int edx, const game::CMidgardID* unit, int* state, bool hold)
{
    const auto ret = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    const auto seq = nextSeq();
    int value = -1;
    readAt(state, 0, value);
    const auto id = safeId(unit);
    const bool on = groupShown(group);
    capturedRecord = nullptr;
    capturedCut = false;
    capturedAllow = -1;
    capturedCurrent = -1;
    origFlash(group, edx, unit, state, hold);
    const char* record = capturedRecord;
    capturedRecord = nullptr;

    const bool steady = value == 7 || value == 8 || value == 10; // 0x64d040
    // «Сразу» - только если 0x64d05f разрешил переход (хук bat_flash_allow; без него - как прежде, set).
    const char* mode = steady ? (capturedAllow == 0 ? "none" : "set") : record ? "flash" : "none";
    std::string extra;
    // Юнит в группе нашёлся - запись вспышки есть; у «сразу» (7, 8, 10) - по ответу 0x64d05f (звали - нашёлся).
    const char* found = record ? "true" : steady ? (capturedAllow >= 0 ? "true" : "null") : "false";
    if (record) {
        std::uint8_t big = 0;
        int ticks = -1;
        int before = -1;
        readAt(record, 0xc, big);
        readAt(record, 0x24, ticks);
        readAt(record, 0x20, before);
        extra = fmt::format(R"(,"big":{},"ticks":{},"before":{})", big != 0, ticks, before);
        if (capturedCut)
            ended(flashes, "bat_flash_end", record, "cut", {});
        std::lock_guard<std::mutex> lock{flashMutex};
        flashes[record] = Shown{id, value, journalMs()};
    } else if (steady && capturedCurrent >= 0) {
        extra = fmt::format(R"(,"big":null,"ticks":{},"before":{})", groupTicks(group), capturedCurrent);
    } else {
        extra = fmt::format(R"(,"big":null,"ticks":{},"before":null)", groupTicks(group));
    }
    countCall("bat_flash");
    emit(seq, fmt::format(R"("ev":"bat_flash","unit":"{}","state":{},"color":{},"mode":"{}","src":"{}","hold":{},"on":{},"found":{}{},"ret_addr":"{}")",
                          id, value, colorOf(value), mode, flashSource(ret), hold, on, found, extra, hexAddress(ret)));
}

using SetState = void(__fastcall*)(void* group, int edx, const game::CMidgardID* unit, int* state);
SetState origSetState{};

/** 0x64d2c3: стойкое состояние портрета (итог опыта 0x64d4a4). */
void __fastcall setStateProbe(void* group, int edx, const game::CMidgardID* unit, int* state)
{
    const auto ret = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    const auto seq = nextSeq();
    int value = -1;
    readAt(state, 0, value);
    const auto id = safeId(unit);
    const bool on = groupShown(group);
    origSetState(group, edx, unit, state);
    const char* src = ret == 0x64d5f5 ? "xp_up" : ret == 0x64d63c ? "xp" : "other";
    countCall("bat_flash");
    emit(seq, fmt::format(R"("ev":"bat_flash","unit":"{}","state":{},"color":{},"mode":"set","src":"{}","hold":true,"on":{},"found":null,"big":null,"ticks":-1,"before":null,"ret_addr":"{}")",
                          id, value, colorOf(value), src, on, hexAddress(ret)));
}

using TextImage = void*(__stdcall*)(bool big, const char* text);
TextImage origTextImage{};

/** 0x64d160: картинка надписи портрета - запомнить «большой» для строки bat_text. */
void* __stdcall textImageProbe(bool big, const char* text)
{
    capturedBig = big ? 1 : 0;
    return origTextImage(big, text);
}

std::string textRect(int big)
{
    if (big < 0)
        return R"(,"big":null,"w":null,"h":null)";
    int w = big ? 150 : 70;
    int h = 85;
    readAt(reinterpret_cast<const void*>(big ? 0x6ebed8 : 0x6ebedc), 0, w);
    readAt(reinterpret_cast<const void*>(0x6ebee0), 0, h);
    return fmt::format(R"(,"big":{},"w":{},"h":{})", big != 0, w, h);
}

const char* textSource(std::uintptr_t ret)
{
    switch (ret) {
    case 0x63bcc2:
        return "target";
    case 0x63718e:
        return "actor";
    case 0x63c34f:
        return "heal";
    default:
        return "other";
    }
}

using PortraitText = void(__fastcall*)(void* group, int edx, const game::CMidgardID* unit, const char* text);
PortraitText origPortraitText{};
PortraitText origSteadyText{};

/** found - юнит есть в этой группе (надпись встала): опыт конца боя зовётся на все четыре группы панели. */
void emitText(std::uint64_t seq, const std::string& id, const char* text, const char* src, bool persist,
              bool on, bool found)
{
    countCall("bat_text");
    emit(seq, fmt::format(R"("ev":"bat_text","unit":"{}","text":"{}","src":"{}","persist":{},"on":{},"found":{}{})", id,
                          readable(text, 1) ? jsonCString(text, 160) : std::string{}, src, persist, on, found,
                          textRect(capturedBig)));
}

/** 0x64d09c: надпись на портрете со сроком (схема 7.3; с 7.6 - место надписи и её конец). */
void __fastcall portraitTextProbe(void* group, int edx, const game::CMidgardID* unit, const char* text)
{
    const auto ret = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    const auto seq = nextSeq();
    const auto id = safeId(unit);
    const bool on = groupShown(group);
    capturedRecord = nullptr;
    capturedCut = false;
    capturedBig = -1;
    origPortraitText(group, edx, unit, text);
    const char* record = capturedRecord;
    if (record) {
        if (capturedCut)
            ended(notes, "bat_text_end", record, "cut", {});
        std::lock_guard<std::mutex> lock{flashMutex};
        notes[record] = Shown{id, -1, journalMs()};
    }
    capturedRecord = nullptr;
    emitText(seq, id, text, textSource(ret), false, on, record != nullptr);
    capturedBig = -1;
}

/** 0x64d23e: стойкая надпись (итог опыта, «Опыт: +N»). */
void __fastcall steadyTextProbe(void* group, int edx, const game::CMidgardID* unit, const char* text)
{
    const auto seq = nextSeq();
    const auto id = safeId(unit);
    const bool on = groupShown(group);
    capturedBig = -1;
    origSteadyText(group, edx, unit, text);
    emitText(seq, id, text, "xp", true, on, capturedBig >= 0);
    capturedBig = -1;
}

} // namespace

} // namespace probe

namespace hooks {

void addBattleFaceHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    const GogHook entries[] = {
        {"bat_text", 0x64d09c, (void*)portraitTextProbe, (void**)&origPortraitText},
        {"bat_text_xp", 0x64d23e, (void*)steadyTextProbe, (void**)&origSteadyText},
        {"bat_text_img", 0x64d160, (void*)textImageProbe, (void**)&origTextImage},
        {"bat_flash", 0x64cee8, (void*)flashProbe, (void**)&origFlash},
        {"bat_flash_set", 0x64d2c3, (void*)setStateProbe, (void**)&origSetState},
        {"bat_flash_rec", 0x64c24c, (void*)releaseProbe, (void**)&origRelease},
        {"bat_flash_tick", 0x64d467, (void*)tickProbe, (void**)&origTick},
        {"bat_flash_allow", 0x64d05f, (void*)allowProbe, (void**)&origAllow},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
}

} // namespace hooks
