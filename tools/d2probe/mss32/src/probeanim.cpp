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
 * Схема 7.3: анимации карты, кадры, курсор, прокрутка вида - что видно, агрегатами (строка на анимацию, на
 * смену курсора, на прокрутку, на 10 секунд кадров), а не на кадр. Разбор - capstone, ранг 1, если не
 * сказано иное (docs/probe-av.md, «Анимации карты»).
 *
 * map_anim - очередь задач карты Impl@CMidAnim2System:
 *  - тик 0x40ae5f __thiscall(Impl*, bool fast) ret 4: одна группа (66 мс медленная, 33 мс быстрая) двигает
 *    текущую задачу; текущая - [Impl+0x1c]/[Impl+0x20] (итератор), «не начата» - итератор равен стражу
 *    [Impl+0xc]. Хук сравнивает текущую до и после: новая - старт (t), была до вызова и её группа
 *    ([[A+4]], байт) совпала с fast - тик.
 *  - снятие законченной 0x40b355 __thiscall(список = Impl+8), ret 0: A = [[[ecx+4]]+0xc], на входе ещё
 *    жива. Строка map_anim с классом по RTTI (CMidAnim2Battle, ...Unit, ...Spell, ...ChangeFog,
 *    CMidAnimSmoothScroll ...), мс от старта, тиками своей группы, кадрами картинки ([[A+8]+4] у
 *    наследников CMidAnim2Animation), клеткой: ход отряда CMidAnim2Unit - D=[A+8], отряд [D+4], куда
 *    [D+0x5c]/[D+0x60]; прокрутка - цель [D+0x14]/[D+0x18]; туман - центр [D+8]/[D+0xc].
 *    Задачи, снятые деструктором системы без конца (уход с карты), строки не дают.
 *
 * frames - конец кадра CRendererImpl 0x5179f0 __thiscall(R+4), ret 0, раз за кадр: число кадров и самый
 * долгий за 10 с; аниматор [ecx+0x7c]: S=[аниматор], группы [S] (66 мс) и [S+4] (33 мс) - счётчик тиков
 * +0x3c и число анимаций +8 (разности за окно). Тик групп без догона: тиков меньше 15/30 в секунду -
 * кадр дольше периода.
 *
 * cursor - показ курсора 0x532f69 __thiscall(держатель, ...), ret 8, на каждое движение мыши: строка только
 * при смене держателя. Имя - из словаря держателей: курсор карты 0x5b27ee __stdcall(int* номер) ret 4
 * (имя char* [0x6ec008 + 4·номер]) и боя 0x5b345e __stdcall(int* номер, bool анимированный) ret 8 (имя -
 * строка 0x6ec743 + 0x16·номер, у анимированного 0x6ec738); прочие держатели - "?" (окна, прицелы задач).
 *
 * view - смена центра вида 0x540d37 __stdcall(слушатели, MapGraphics*, CMqPoint* клетка) ret 0xc (общая
 * точка центрирования 0x540c38 и ручного шага 0x541271): смены подряд с шагом до 300 мс - одна строка
 * «из, в, шагов, мс»; причина - адрес возврата из 0x540c38 (или "manual").
 */

namespace probe {

namespace {

std::mutex animMutex;

// ---------------------------------------------------------------- map_anim

struct MapAnim
{
    std::uint64_t startMs{};
    long long ticks{};
};

std::map<const void*, MapAnim> mapAnims;

/** Текущая задача системы карты (или nullptr - не начата / не прочитать). */
const void* currentAnim(const void* impl)
{
    std::uintptr_t iter = 0;
    std::uintptr_t alt = 0;
    std::uintptr_t guard = 0;
    if (!readAt(impl, 0x1c, iter) || !readAt(impl, 0x20, alt) || !readAt(impl, 0xc, guard))
        return nullptr;
    if (alt == 0 && iter == guard)
        return nullptr;

    const auto value = alt ? alt : iter + 8;
    const void* anim = nullptr;
    return readAt(reinterpret_cast<const void*>(value), 4, anim) ? anim : nullptr;
}

/** Байт группы задачи [[A+4]] или -1. */
int groupOf(const void* anim)
{
    const void* data = nullptr;
    std::uint8_t group = 0;
    return readAt(anim, 4, data) && readAt(data, 0, group) ? group : -1;
}

using AnimTick = void(__fastcall*)(void* impl, int edx, bool fast);
AnimTick origAnimTick{};

void __fastcall animTickProbe(void* impl, int edx, bool fast)
{
    const void* before = currentAnim(impl);
    const bool ticked = before && groupOf(before) == static_cast<int>(fast);
    origAnimTick(impl, edx, fast);
    const void* after = currentAnim(impl);

    std::lock_guard<std::mutex> lock{animMutex};
    if (ticked) {
        const auto it = mapAnims.find(before);
        if (it != mapAnims.end())
            ++it->second.ticks;
    }
    if (after && after != before)
        mapAnims[after] = MapAnim{journalMs(), 0};
}

/** ",\"x\":..,\"y\":..,\"obj\":.." - клетка и объект задачи по классу. */
std::string animPlace(const void* anim, const std::string& cls)
{
    const void* data = nullptr;
    readAt(anim, 8, data);
    int x = -1;
    int y = -1;
    std::string obj{"null"};
    if (data && cls == "CMidAnim2Unit") {
        readAt(data, 0x5c, x);
        readAt(data, 0x60, y);
        const auto id = reinterpret_cast<const char*>(data) + 4;
        if (readable(id, sizeof(game::CMidgardID)))
            obj = fmt::format(R"("{}")", idToString(reinterpret_cast<const game::CMidgardID*>(id)));
    } else if (data && cls == "CMidAnimSmoothScroll") {
        readAt(data, 0x14, x);
        readAt(data, 0x18, y);
    } else if (data && cls == "CMidAnim2ChangeFog") {
        readAt(data, 8, x);
        readAt(data, 0xc, y);
    }
    const auto num = [](int v) { return v < 0 ? std::string{"null"} : std::to_string(v); };
    return fmt::format(R"("x":{},"y":{},"obj":{})", num(x), num(y), obj);
}

using AnimRemove = void(__fastcall*)(void* list, int edx);
AnimRemove origAnimRemove{};

void __fastcall animRemoveProbe(void* list, int edx)
{
    const void* head = nullptr;
    const void* node = nullptr;
    const void* anim = nullptr;
    if (readAt(list, 4, head) && readAt(head, 0, node) && readAt(node, 0xc, anim) && anim) {
        MapAnim rec{};
        bool known = false;
        {
            std::lock_guard<std::mutex> lock{animMutex};
            const auto it = mapAnims.find(anim);
            if (it != mapAnims.end()) {
                rec = it->second;
                known = true;
                mapAnims.erase(it);
            }
        }
        const auto cls = rttiClassName(anim);
        int frames = -1;
        const void* data = nullptr;
        if (cls != "CMidAnim2Unit" && cls != "CMidAnimSmoothScroll" && cls != "CMidAnim2ChangeFog"
            && readAt(anim, 8, data))
            readAt(data, 4, frames);
        countCall("map_anim");
        emit(nextSeq(),
             fmt::format(
                 R"("ev":"map_anim","action":"end","class":"{}",{},"ms":{},"ticks":{},"frames":{},"group":{},"started":{})",
                 cls, animPlace(anim, cls), known ? journalMs() - rec.startMs : 0, rec.ticks,
                 frames < 0 || frames > 10000 ? std::string{"null"} : std::to_string(frames),
                 groupOf(anim), known));
    }
    origAnimRemove(list, edx);
}

// ---------------------------------------------------------------- frames

constexpr std::uint64_t framesWindowMs = 10000;

struct FrameWindow
{
    LARGE_INTEGER last{};
    std::uint64_t windowStart{};
    long long frames{};
    double maxMs{};
    long long slowTicks{-1};
    long long fastTicks{-1};
};

FrameWindow frameWindow;
double qpcPerMs = 0;

/** Тики и число анимаций группы аниматора: S=[аниматор], группа [S+4·i]. */
bool groupState(const void* animator, int index, long long& ticks, long long& count)
{
    const void* s = nullptr;
    const void* group = nullptr;
    std::uint32_t t = 0;
    std::uint32_t n = 0;
    if (!readAt(animator, 0, s) || !readAt(s, 4 * index, group) || !readAt(group, 0x3c, t)
        || !readAt(group, 8, n))
        return false;
    ticks = t;
    count = n;
    return true;
}

using EndFrame = void(__fastcall*)(void* renderer, int edx);
EndFrame origEndFrame{};

void __fastcall endFrameProbe(void* renderer, int edx)
{
    origEndFrame(renderer, edx);

    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    auto& w = frameWindow;
    if (qpcPerMs == 0) {
        LARGE_INTEGER freq{};
        QueryPerformanceFrequency(&freq);
        qpcPerMs = static_cast<double>(freq.QuadPart) / 1000.0;
    }
    if (w.last.QuadPart != 0) {
        const double ms = static_cast<double>(now.QuadPart - w.last.QuadPart) / qpcPerMs;
        w.maxMs = (std::max)(w.maxMs, ms);
    }
    w.last = now;
    ++w.frames;

    const auto t = journalMs();
    if (w.windowStart == 0) {
        w.windowStart = t;
        return;
    }
    if (t - w.windowStart < framesWindowMs)
        return;

    const void* animator = nullptr;
    long long slowTicks = -1, slowN = -1, fastTicks = -1, fastN = -1;
    if (readAt(renderer, 0x7c, animator)) {
        groupState(animator, 0, slowTicks, slowN);
        groupState(animator, 1, fastTicks, fastN);
    }
    const auto delta = [](long long now, long long before) {
        return now < 0 || before < 0 ? std::string{"null"} : std::to_string(now - before);
    };
    countCall("frames");
    emit(nextSeq(),
         fmt::format(
             R"("ev":"frames","n":{},"ms":{},"max_ms":{},"slow_ticks":{},"fast_ticks":{},"slow_n":{},"fast_n":{},{})",
             w.frames, t - w.windowStart, static_cast<long long>(w.maxMs + 0.5),
             delta(slowTicks, w.slowTicks), delta(fastTicks, w.fastTicks), slowN, fastN,
             screenFields()));
    w.windowStart = t;
    w.frames = 0;
    w.maxMs = 0;
    w.slowTicks = slowTicks;
    w.fastTicks = fastTicks;
}

// ---------------------------------------------------------------- cursor

std::map<const void*, std::string> cursorNames;
// Держатели курсоров создаются раз (карта, бой - кэш задач); потолок - на случай, если движок создаёт их на каждый вызов.
constexpr std::size_t maxCursorNames = 4096;
const void* lastCursor{nullptr};

using MapCursor = void*(__stdcall*)(const int* number);
using BattleCursor = void*(__stdcall*)(const int* number, bool animated);
MapCursor origMapCursor{};
BattleCursor origBattleCursor{};

void* __stdcall mapCursorProbe(const int* number)
{
    void* holder = origMapCursor(number);
    int n = -1;
    const char* name = nullptr;
    if (holder && readAt(number, 0, n) && n >= 0 && n < 64
        && readAt(reinterpret_cast<const void*>(0x6ec008 + 4 * n), 0, name) && name) {
        std::lock_guard<std::mutex> lock{animMutex};
        if (cursorNames.size() < maxCursorNames || cursorNames.count(holder))
            cursorNames[holder] = jsonCString(name, 32);
    }
    return holder;
}

void* __stdcall battleCursorProbe(const int* number, bool animated)
{
    void* holder = origBattleCursor(number, animated);
    int n = -1;
    if (holder && readAt(number, 0, n) && n >= 0 && n < 16) {
        const auto* name = reinterpret_cast<const char*>((animated ? 0x6ec738 : 0x6ec743) + 0x16 * n);
        std::lock_guard<std::mutex> lock{animMutex};
        if (cursorNames.size() < maxCursorNames || cursorNames.count(holder))
            cursorNames[holder] = jsonCString(name, 0x16);
    }
    return holder;
}

using ShowCursor = void(__fastcall*)(void* holder, int edx, int a1, void* point);
ShowCursor origShowCursor{};

void __fastcall showCursorProbe(void* holder, int edx, int a1, void* point)
{
    if (holder != lastCursor) {
        lastCursor = holder;
        std::string name{"?"};
        {
            std::lock_guard<std::mutex> lock{animMutex};
            const auto it = cursorNames.find(holder);
            if (it != cursorNames.end())
                name = it->second;
        }
        countCall("cursor");
        emit(nextSeq(), fmt::format(R"("ev":"cursor","name":"{}","holder":"{}",{})", name,
                                    hexAddress(reinterpret_cast<std::uintptr_t>(holder)),
                                    screenFields()));
    }
    origShowCursor(holder, edx, a1, point);
}

// ---------------------------------------------------------------- view

struct ViewRun
{
    int fromX{-1}, fromY{-1}, toX{-1}, toY{-1};
    int steps{};
    std::uint64_t startMs{}, lastMs{};
    std::uintptr_t cause{};
};

ViewRun viewRun;
int viewX = -1;
int viewY = -1;
thread_local std::uintptr_t centerCaller{0};
constexpr std::uint64_t viewGapMs = 300;

std::string takeView()
{
    auto& r = viewRun;
    if (r.steps == 0)
        return {};
    auto line = fmt::format(
        R"("ev":"view","from":[{},{}],"to":[{},{}],"steps":{},"ms":{},"cause":"{}")", r.fromX, r.fromY,
        r.toX, r.toY, r.steps, r.lastMs - r.startMs,
        r.cause == 1 ? std::string{"manual"} : hexAddress(r.cause));
    r.steps = 0;
    return line;
}

using ViewMoved = void(__stdcall*)(void* listeners, void* graphics, const int* cell);
ViewMoved origViewMoved{};

void __stdcall viewMovedProbe(void* listeners, void* graphics, const int* cell)
{
    int x = -1;
    int y = -1;
    readAt(cell, 0, x);
    readAt(cell, 4, y);
    std::string line;
    {
        std::lock_guard<std::mutex> lock{animMutex};
        const auto now = journalMs();
        auto& r = viewRun;
        const auto cause = centerCaller ? centerCaller : 1;
        if (r.steps > 0 && (now - r.lastMs > viewGapMs || r.cause != cause))
            line = takeView();
        if (r.steps == 0) {
            r.fromX = viewX;
            r.fromY = viewY;
            r.startMs = now;
            r.cause = cause;
        }
        ++r.steps;
        r.lastMs = now;
        r.toX = viewX = x;
        r.toY = viewY = y;
    }
    if (!line.empty()) {
        countCall("view");
        emit(nextSeq(), line);
    }
    origViewMoved(listeners, graphics, cell);
}

using CenterView = void(__fastcall*)(void* graphics, int edx, void* cell, int dx, int dy);
CenterView origCenterView{};

void __fastcall centerViewProbe(void* graphics, int edx, void* cell, int dx, int dy)
{
    ScopedValue<std::uintptr_t> scope{centerCaller, reinterpret_cast<std::uintptr_t>(_ReturnAddress())};
    origCenterView(graphics, edx, cell, dx, dy);
}

} // namespace

void flushViewRun()
{
    std::string line;
    {
        std::unique_lock<std::mutex> lock{animMutex, std::try_to_lock};
        if (!lock.owns_lock())
            return;
        line = takeView();
    }
    if (!line.empty()) {
        countCall("view");
        emit(nextSeq(), line);
    }
}

} // namespace probe

namespace hooks {

void addAnimHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    const GogHook entries[] = {
        {"map_anim_tick", 0x40ae5f, (void*)animTickProbe, (void**)&origAnimTick},
        {"map_anim_end", 0x40b355, (void*)animRemoveProbe, (void**)&origAnimRemove},
        {"frames", 0x5179f0, (void*)endFrameProbe, (void**)&origEndFrame},
        {"cursor_map", 0x5b27ee, (void*)mapCursorProbe, (void**)&origMapCursor},
        {"cursor_battle", 0x5b345e, (void*)battleCursorProbe, (void**)&origBattleCursor},
        {"cursor", 0x532f69, (void*)showCursorProbe, (void**)&origShowCursor},
        {"view", 0x540d37, (void*)viewMovedProbe, (void**)&origViewMoved},
        {"view_center", 0x540c38, (void*)centerViewProbe, (void**)&origCenterView},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
}

} // namespace hooks
