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
#include <utility>
#include <vector>
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

/*
 * Схема 7.3: показ боя - анимации юнитов, эффекты, касания, порядок показа, надписи (docs/probe-av.md, «Бой»;
 * разбор capstone, ранг 1, если не сказано иное). Строка на смену анимации и на событие показа, не на кадр.
 *
 * bat_anim - любая картинка юнита встаёт на место через CBatViewer2DEngine::addUnitImage 0x646ec5
 * __thiscall(движок, IMqImage2* img, CMqPoint*, bool слева, int* подслой, int позиция, bool) ret 0x18. Вид - по
 * адресу возврата: 0x653e10 покой (IDLE, STIL без анимаций боя - байт D+0xff4), 0x653a01 труп DEAD, 0x6547da
 * замах HMOV, 0x65424f удар HHIT, 0x652006 предмет ITEM, 0x652932 эффект Battle.ff EFFECT. Юнит - CBatUnitAnim,
 * чей метод сейчас идёт (покой 0x6537f6 ret 8, замах 0x654346 ret 0, удар 0x653fb7 ret 0, предмет 0x651c9c ret 4,
 * эффект 0x65255b ret 0x10 - хуки кладут ecx в потоковую переменную): D=[ecx+4], id юнита D+0xc, id типа
 * картинок D+0x10. Кадры - img->vtbl[+0x10] (ранг 2: смысл «число кадров»). Покой переставляется всем юнитам
 * на каждом обновлении вида, поэтому строка - только при смене (вид, картинка) на месте (слева, позиция).
 *
 * bat_fx - эффект ходящего HEFF (тот же 0x646ec5 с возвратом 0x63a177, тень 0x63a236 не пишется) и картинка
 * на месте 0x636239 __stdcall(атака, позиция, rect*, bool слева, bool большой, functor*, holder*, a8, a9) ret
 * 0x24: возврат 0x63a8fb - TUCH на цели, 0x63618a - тик яда/мороза/ожога TICK, 0x63c471 - лечение переливом
 * HEAL; кадры - [holder+8]->vtbl[+0x10]. Юнит строки - ходящий текущего шага.
 *
 * bat_show - шаг просмотра IBatViewer::showAttackEffect 0x630a7b __thiscall(interf+0x18, BattleMsgData*,
 * holder*, строка) ret 0xc: R=[holder], R+0 ходящий, R+8 вид шага 0..16 (0 атака, 1 пас, 2-3 отступление,
 * 4-5 снятие паралича/окаменения, 6-8 яд/мороз/ожог, 9 ждать, 10 защита, 12 предмет, 13/16 откат облика/уровня,
 * 14 снятие призыва, 15 рост), R+0xc номер атаки, цели - список R+0x14 (head [R+0x18], запись n+8: +0 id,
 * +8 промах, +9 иммунитет/оберег, +0xc урон). Конец шага - 0x638183 __thiscall(bool) ret 4 с адресом
 * возврата 0x63751e (счётчик анимаций шага V+0x14d8 дошёл до нуля): строка bat_show_end с мс шага.
 *
 * bat_text - надпись на портрете 0x64d09c: с 7.6 - в probeflash.cpp вместе со вспышками портретов, местом
 * надписи и её концом.
 */

namespace probe {

namespace {

std::mutex batMutex;

thread_local const void* currentUnitAnim{nullptr};

/** id юнита и его типа из CBatUnitAnim (D=[anim+4], +0xc, +0x10) как JSON или null. */
std::pair<std::string, std::string> unitOfAnim(const void* anim)
{
    const char* data = nullptr;
    if (!anim || !readAt(anim, 4, data) || !data)
        return {"null", "null"};
    const auto id = [&](std::size_t offset) {
        const auto* p = data + offset;
        return readable(p, sizeof(game::CMidgardID))
                   ? fmt::format(R"("{}")", idToString(reinterpret_cast<const game::CMidgardID*>(p)))
                   : std::string{"null"};
    };
    return {id(0xc), id(0x10)};
}

/** Кадров картинки боя (IMqImage2 vtbl+0x10) или -1. */
int imageFrames(const void* image)
{
    // vtbl+0x10 без аргументов: обёртки CImage2Movable/CImage2Callback (0x55e567) передают его внутренней картинке
    // хвостовым jmp без своих аргументов, движок зовёт так же (0x6529c3, 0x6537f6); смысл «число кадров» - ранг 2.
    void* const* vtable = nullptr;
    void* fn = nullptr;
    if (!image || !readAt(image, 0, vtable) || !readAt(vtable, 0x10, fn) || !fn || !readable(fn, 1))
        return -1;
    using Frames = int(__thiscall*)(const void*);
    return reinterpret_cast<Frames>(fn)(image);
}

/** Ходящий текущего шага показа (JSON) и начало шага. */
std::string showUnit{"null"};
long long showStep{-1};
std::uint64_t showStartMs{};

/** Последняя картинка на месте (слева, позиция): вид и указатель - дедупликация покоя. */
std::map<int, std::pair<std::string, const void*>> slotImage;

using UnitImage = void(__fastcall*)(void* engine, int edx, void* image, void* point, bool left, int* sub,
                                    int pos, bool flag);
UnitImage origUnitImage{};

const char* unitKind(std::uintptr_t ret, const void* anim)
{
    switch (ret) {
    case 0x653e10: {
        const char* data = nullptr;
        std::uint8_t animations = 1;
        if (readAt(anim, 4, data) && data)
            readAt(data, 0xff4, animations);
        return animations ? "IDLE" : "STIL";
    }
    case 0x653a01:
        return "DEAD";
    case 0x6547da:
        return "HMOV";
    case 0x65424f:
        return "HHIT";
    case 0x652006:
        return "ITEM";
    case 0x652932:
        return "EFFECT";
    default:
        return nullptr;
    }
}

void emitFx(const char* kind, int frames, bool left, int pos)
{
    std::string unit;
    {
        std::lock_guard<std::mutex> lock{batMutex};
        unit = showUnit;
    }
    countCall("bat_fx");
    emit(nextSeq(), fmt::format(R"("ev":"bat_fx","kind":"{}","unit":{},"name":null,"frames":{},"left":{},"pos":{})",
                                kind, unit, frames, left, pos));
}

void __fastcall unitImageProbe(void* engine, int edx, void* image, void* point, bool left, int* sub,
                               int pos, bool flag)
{
    origUnitImage(engine, edx, image, point, left, sub, pos, flag);
    const auto ret = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    if (ret == 0x63a177) {
        emitFx("HEFF", imageFrames(image), left, pos);
        return;
    }
    const auto* anim = currentUnitAnim;
    const char* kind = unitKind(ret, anim);
    if (!kind)
        return;

    const int slot = (left ? 100 : 0) + pos;
    {
        std::lock_guard<std::mutex> lock{batMutex};
        auto& last = slotImage[slot];
        if (last.first == kind && last.second == image)
            return;
        last = {kind, image};
    }
    const auto [unit, type] = unitOfAnim(anim);
    int layer = -1;
    readAt(sub, 0, layer);
    countCall("bat_anim");
    emit(nextSeq(), fmt::format(R"("ev":"bat_anim","unit":{},"type":{},"kind":"{}","frames":{},"left":{},"pos":{},"sub":{})",
                                unit, type, kind, imageFrames(image), left, pos, layer));
}

// Методы CBatUnitAnim, внутри которых картинка встаёт на место: запомнить юнита (ecx) для bat_anim.
template <typename Call>
auto withUnitAnim(const void* anim, Call&& call)
{
    const auto* outer = currentUnitAnim;
    currentUnitAnim = anim;
    struct Restore
    {
        const void* value;
        ~Restore()
        {
            currentUnitAnim = value;
        }
    } restore{outer};
    return call();
}

using AnimIdle = void(__fastcall*)(void* anim, int edx, int a1, int a2);
using AnimNoArgs = void(__fastcall*)(void* anim, int edx);
using AnimItem = void(__fastcall*)(void* anim, int edx, int a1);
using AnimEffect = bool(__fastcall*)(void* anim, int edx, void* bmd, void* look, int code, int pause);
AnimIdle origAnimIdle{};
AnimNoArgs origAnimSwing{};
AnimNoArgs origAnimHurt{};
AnimItem origAnimItem{};
AnimEffect origAnimEffect{};

void __fastcall animIdleProbe(void* anim, int edx, int a1, int a2)
{
    withUnitAnim(anim, [&] { origAnimIdle(anim, edx, a1, a2); });
}

void __fastcall animSwingProbe(void* anim, int edx)
{
    withUnitAnim(anim, [&] { origAnimSwing(anim, edx); });
}

void __fastcall animHurtProbe(void* anim, int edx)
{
    withUnitAnim(anim, [&] { origAnimHurt(anim, edx); });
}

void __fastcall animItemProbe(void* anim, int edx, int a1)
{
    withUnitAnim(anim, [&] { origAnimItem(anim, edx, a1); });
}

bool __fastcall animEffectProbe(void* anim, int edx, void* bmd, void* look, int code, int pause)
{
    const bool shown = withUnitAnim(anim, [&] { return origAnimEffect(anim, edx, bmd, look, code, pause); });
    const auto [unit, type] = unitOfAnim(anim);
    const char* data = nullptr;
    int after = -1;
    if (readAt(anim, 4, data) && data)
        readAt(data, 0x2c, after);
    countCall("bat_effect");
    emit(nextSeq(), fmt::format(R"("ev":"bat_effect","unit":{},"type":{},"code":{},"code_after":{},"pause":{},"shown":{})",
                                unit, type, code, after, pause, shown));
    return shown;
}

using SpotImage = bool(__stdcall*)(int attack, int pos, void* rect, bool left, bool big, void* functor,
                                  void* holder, int a8, int a9);
SpotImage origSpotImage{};

bool __stdcall spotImageProbe(int attack, int pos, void* rect, bool left, bool big, void* functor,
                             void* holder, int a8, int a9)
{
    const bool shown = origSpotImage(attack, pos, rect, left, big, functor, holder, a8, a9);
    const auto ret = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    const char* kind = ret == 0x63a8fb ? "TUCH" : ret == 0x63618a ? "TICK" : ret == 0x63c471 ? "HEAL" : "SPOT";
    const void* image = nullptr;
    readAt(holder, 8, image);
    if (shown)
        emitFx(kind, imageFrames(image), left, pos);
    return shown;
}

using ShowStep = void(__fastcall*)(void* viewer, int edx, void* bmd, void* holder, void* text);
ShowStep origShowStep{};

/** Цели записи шага: [{id, miss, imm, dmg}], не больше 12. */
std::string stepTargets(const char* record)
{
    std::string out{"["};
    const char* head = nullptr;
    if (!readAt(record, 0x18, head) || !head)
        return "null";
    const char* node = nullptr;
    readAt(head, 0, node);
    const char* sep = "";
    for (int i = 0; node && node != head && i < 12; ++i) {
        const char* rec = node + 8;
        std::uint8_t miss = 0;
        std::uint8_t immune = 0;
        int damage = 0;
        readAt(rec, 8, miss);
        readAt(rec, 9, immune);
        readAt(rec, 0xc, damage);
        const auto id = readable(rec, sizeof(game::CMidgardID))
                            ? fmt::format(R"("{}")", idToString(reinterpret_cast<const game::CMidgardID*>(rec)))
                            : std::string{"null"};
        out += fmt::format(R"({}{{"id":{},"miss":{},"imm":{},"dmg":{}}})", sep, id, miss != 0, immune != 0, damage);
        sep = ",";
        const char* next = nullptr;
        if (!readAt(node, 0, next))
            break;
        node = next;
    }
    return out + "]";
}

void __fastcall showStepProbe(void* viewer, int edx, void* bmd, void* holder, void* text)
{
    const char* record = nullptr;
    readAt(holder, 0, record);
    std::string unit{"null"};
    int step = -1;
    int attack = -1;
    std::string targets{"null"};
    if (record) {
        if (readable(record, sizeof(game::CMidgardID)))
            unit = fmt::format(R"("{}")", idToString(reinterpret_cast<const game::CMidgardID*>(record)));
        readAt(record, 8, step);
        readAt(record, 0xc, attack);
        targets = stepTargets(record);
    }
    {
        std::lock_guard<std::mutex> lock{batMutex};
        showUnit = unit;
        showStep = step;
        showStartMs = journalMs();
    }
    countCall("bat_show");
    emit(nextSeq(), fmt::format(R"("ev":"bat_show","step":{},"unit":{},"attack":{},"targets":{})", step, unit,
                                attack, targets));
    origShowStep(viewer, edx, bmd, holder, text);
}

using StepDone = void(__fastcall*)(void* viewer, int edx, int a1);
StepDone origStepDone{};

void __fastcall stepDoneProbe(void* viewer, int edx, int a1)
{
    if (reinterpret_cast<std::uintptr_t>(_ReturnAddress()) == 0x63751e) {
        std::string unit;
        long long step = -1;
        std::uint64_t ms = 0;
        {
            std::lock_guard<std::mutex> lock{batMutex};
            unit = showUnit;
            step = showStep;
            ms = journalMs() - showStartMs;
        }
        countCall("bat_show");
        emit(nextSeq(), fmt::format(R"("ev":"bat_show_end","step":{},"unit":{},"ms":{})", step, unit, ms));
    }
    origStepDone(viewer, edx, a1);
}

} // namespace

} // namespace probe

namespace hooks {

void addBattleShowHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    const GogHook entries[] = {
        {"bat_anim", 0x646ec5, (void*)unitImageProbe, (void**)&origUnitImage},
        {"bat_anim_idle", 0x6537f6, (void*)animIdleProbe, (void**)&origAnimIdle},
        {"bat_anim_swing", 0x654346, (void*)animSwingProbe, (void**)&origAnimSwing},
        {"bat_anim_hurt", 0x653fb7, (void*)animHurtProbe, (void**)&origAnimHurt},
        {"bat_anim_item", 0x651c9c, (void*)animItemProbe, (void**)&origAnimItem},
        {"bat_effect", 0x65255b, (void*)animEffectProbe, (void**)&origAnimEffect},
        {"bat_fx", 0x636239, (void*)spotImageProbe, (void**)&origSpotImage},
        {"bat_show", 0x630a7b, (void*)showStepProbe, (void**)&origShowStep},
        {"bat_show_end", 0x638183, (void*)stepDoneProbe, (void**)&origStepDone},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
}

} // namespace hooks
