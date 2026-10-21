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

#include "gameutils.h"
#include "mapgraphics.h"
#include "midgardid.h"
#include "midgardplan.h"
#include "mqpoint.h"
#include "probe.h"
#include "smartptr.h"
#include <cstdint>
#include <fmt/format.h>
#include <string>
#include <vector>

/*
 * Схема 7.1, часть 3: щелчок мышью по виду карты - map_click.
 *
 * Адреса - Discipl2.exe Gog 3.01, capstone (ранг 1, если не сказано иное).
 *
 * 0x48e3da - CStratInterf::handleMouse, слот 3 vftable 0x6d4924 (часть CInterface лежит в объекте
 * по смещению 4: 0x48e477 зовёт 0x48ebe3 с ecx = this - 4), int __thiscall(msg, CMqPoint* точка),
 * ret 8, пролог push ebp; mov ebp, esp; sub esp, 0x10. При [this+0xf0] > 0 щелчок не
 * обрабатывается вовсе (0x48e3e5). Иначе - базовый CFullScreenInterf::handleMouse 0x5c9374: дети
 * (кнопки, панели - 0x53055b) и текущая задача вида (ITask слот 2) в порядке по флагу
 * [[this+0xc]+4]; если никто не взял - WM_LBUTTONUP в прямоугольнике [0x799f78] (0x48ebe3) и
 * WM_RBUTTONDOWN - всплывающее окно по точке карты (0x48e4d6).
 *
 * Клетку щелчка считает задача: CTaskStrat::handleMouse 0x4d3f71 (vftable 0x6daad4 слот 2) на
 * WM_LBUTTONDOWN берёт getMapGraphics 0x5404d9 и зовёт 0x540f6a __thiscall(MapGraphics**,
 * const CMqPoint* мышь, CMqPoint* клетка, CMqPoint* сдвиг или 0), ret 0xc: false - точка не на
 * карте (функтор CStratInterf canConvertMouse или клетка за краем), иначе клетка. Дальше
 * 0x4d4331(клетка) - отряд в клетке, 0x4d45a6(клетка) - путь/ход. Прочие задачи карты (выбор
 * юнита, заклинание, жезл, подбор, обмен, выбор города - слоты 2 по vftable 0x6da9bc, 0x6daa44,
 * 0x6da784, 0x6da944, 0x6da7fc, 0x6da97c) тоже реагируют только на 0x201 и обычно зовут 0x4d3f71.
 * Зонд зовёт 0x540f6a так же, как 0x4d3f71 (функция только считает: пишет лишь свои выходы).
 *
 * Текущая задача - без вызовов: taskManager = [[this+0xc]] (CFullScreenInterfData, holder
 * 0x61f5a0), задача = [[taskManager+4]+4] (0x5c8fa1; пустую она создаёт - зонд не зовёт её, а
 * пишет null). Что в клетке «по мнению движка» - CMidgardPlan::getObjectId 0x5f54e2 по типам
 * объектов плана карты (таблица 0x6eefc8: 0 - крупные, 1 - мелкие), карта клиента -
 * hooks::getObjectMap() в потоке интерфейса.
 *
 * Пишутся только нажатия (down, dblclk): все действия карты висят на WM_LBUTTONDOWN (задачи) и
 * WM_RBUTTONDOWN (окно по точке); отпускание дало бы вторую строку на каждый щелчок.
 */

namespace probe {

namespace {

using namespace game;

using HandleMouse = int(__fastcall*)(void* thisptr, int edx, std::uint32_t msg, const CMqPoint* point);
HandleMouse origStratHandleMouse{};

using MouseToCell = bool(__thiscall*)(MapGraphics** thisptr,
                                      const CMqPoint* mouse,
                                      CMqPoint* cell,
                                      CMqPoint* offset);
const auto mouseToCell = reinterpret_cast<MouseToCell>(0x540f6a);

constexpr std::size_t busyOffset = 0xf0;
constexpr std::size_t screenDataOffset = 0xc;

struct MouseMessage
{
    std::uint32_t msg;
    int button;
    const char* action;
};

constexpr MouseMessage mouseMessages[] = {
    {0x201, 1, "down"}, // WM_LBUTTONDOWN
    {0x203, 1, "dblclk"},
    {0x204, 2, "down"}, // WM_RBUTTONDOWN
    {0x206, 2, "dblclk"},
    {0x207, 3, "down"}, // WM_MBUTTONDOWN
    {0x209, 3, "dblclk"},
};

const MouseMessage* findMessage(std::uint32_t msg)
{
    for (const auto& message : mouseMessages) {
        if (message.msg == msg)
            return &message;
    }

    return nullptr;
}

/** Типы объектов плана карты (таблица 0x6eefc8 = 0 или 1), в порядке вывода. */
constexpr IdType planTypes[] = {
    IdType::Stack, IdType::Fortification, IdType::Site,  IdType::Ruin, IdType::Landmark,
    IdType::Crystal, IdType::Bag, IdType::Tomb, IdType::Rod, IdType::Road,
};

/** Клетка под точкой - как у CTaskStrat 0x4d3f71; false - точка не на карте. */
bool cellAt(const CMqPoint& point, CMqPoint& cell)
{
    MapGraphicsPtr graphics{};
    MapGraphicsApi::get().getMapGraphics(&graphics);
    bool found = false;
    if (graphics.data && *graphics.data)
        found = mouseToCell(graphics.data, &point, &cell, nullptr);
    SmartPointerApi::get().createOrFree(reinterpret_cast<SmartPointer*>(&graphics), nullptr);
    return found;
}

/** Id объектов плана в клетке как JSON-массив или null без карты клиента. */
std::string objectsAt(const CMqPoint& cell)
{
    const auto* objectMap = hooks::getObjectMap();
    if (!objectMap || !readableMap(objectMap))
        return {"null"};

    const auto* plan = hooks::getMidgardPlan(objectMap);
    if (!plan)
        return {"null"};

    std::string list;
    for (const auto type : planTypes) {
        const auto* id = CMidgardPlanApi::get().getObjectId(plan, &cell, &type);
        if (!id)
            continue;
        list += fmt::format(R"({}"{}")", list.empty() ? "" : ",", idToString(id));
    }

    return fmt::format("[{}]", list);
}

/** Класс текущей задачи вида (RTTI) как JSON-строка или null, если задачи ещё нет. */
std::string currentTask(const void* interf)
{
    std::uintptr_t data{};
    std::uintptr_t manager{};
    std::uintptr_t holder{};
    std::uintptr_t task{};
    if (!readAt(interf, screenDataOffset, data) || !readAt(reinterpret_cast<const void*>(data), 0, manager)
        || !readAt(reinterpret_cast<const void*>(manager), 4, holder)
        || !readAt(reinterpret_cast<const void*>(holder), 4, task) || !task)
        return {"null"};

    return fmt::format(R"("{}")", rttiClassName(reinterpret_cast<const void*>(task)));
}

int __fastcall stratHandleMouseProbe(void* thisptr, int edx, std::uint32_t msg, const CMqPoint* point)
{
    const auto* message = findMessage(msg);
    if (!message)
        return origStratHandleMouse(thisptr, edx, msg, point);

    const auto seq = nextSeq();
    CMqPoint mouse{-1, -1};
    const bool pointOk = readAt(point, 0, mouse);
    CMqPoint cell{};
    const bool onMap = pointOk && cellAt(mouse, cell);
    const auto cellJson = onMap ? fmt::format(R"({{"x":{},"y":{}}})", cell.x, cell.y)
                                : std::string{"null"};
    const auto objects = onMap ? objectsAt(cell) : std::string{"null"};
    const auto task = currentTask(thisptr);
    int busy = -1;
    readAt(thisptr, busyOffset, busy);

    const int result = origStratHandleMouse(thisptr, edx, msg, point);

    countCall("map_click");
    emit(seq,
         fmt::format(
             R"("ev":"map_click","msg":{},"button":{},"action":"{}","x":{},"y":{},"cell":{},"objects":{},"task":{},"busy":{},"ret":{},{})",
             msg, message->button, message->action, mouse.x, mouse.y, cellJson, objects, task, busy,
             result, screenFields()));
    return result;
}

} // namespace

} // namespace probe

namespace hooks {

void addClickHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    const GogHook entries[] = {
        {"map_click", 0x48e3da, (void*)stratHandleMouseProbe, (void**)&origStratHandleMouse},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
}

} // namespace hooks
