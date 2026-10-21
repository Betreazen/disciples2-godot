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
#include <cstring>
#include <fmt/format.h>
#include <mutex>
#include <string>
#include <utility>
#include <vector>
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

/*
 * Схема 7.6: метки карты - эллипс выбранного отряда и флажки пути (docs/probe-av.md §8, разбор capstone,
 * ранг 1, если не сказано иное). Строка на смену того, что видно, не на перерисовку.
 *
 * Слои (загрузка iisolay 0x5af55e, имя -> глобал): SYM_STACK_SEL 0x838538, SYM_MOVE_PATH 0x838590.
 * Картинку на клетку ставит ShowImageOnMap 0x5ad1ee __stdcall(CMqPoint* клетка, слой*, IMqImage2*, 0, 0)
 * ret 0x14, снимает весь слой HideLayerImages 0x5ad312 __stdcall(слой*) ret 4 (тулсет mapgraphics.cpp).
 *
 * map_sel - эллипс SELECTED: показан (action show, клетка; вызов 0x4d261f - приход отряда 0x4d24b0, 0x4cfb32 -
 * выбор) или снят (clear; вызов 0x4cfadb - выбор другого, 0x4cf53d - выбор города CTaskSelectCity). Пишется только
 * смена (показан ли, клетка); via - по адресу возврата (вызов + 5).
 *
 * map_path - путь: ShowMovementPath 0x4ccc2c __stdcall(карта, CMidgardID* отряд, List<CMqPoint>* путь,
 * CMqPoint* последняя достижимая, CMqPoint* конец, bool) ret 0x18 (тулсет game.cpp) ставит флажок на
 * каждую клетку (0x5ad1ee из 0x4cd08a), картинка флажка - имя в 0x5ae20c __thiscall(образы, char* имя, bool)
 * ret 8 с возвратом 0x4cce7e (MOVENORMAL, MOVEOUT, MOVEACTION, MOVEBATTLE, MOVENEGO, MOVEINCMP - [0x7aea6c..
 * 0x7aea80]). Строка show - отряд, длина пути, флажков, их имена по порядку сериями «ИМЯ*n», первая и
 * последняя клетка, последняя достижимая и конец. Снятие - 0x4cd329 (__thiscall без аргументов, push
 * SYM_MOVE_PATH; call 0x5ad312): via по возврату - 0x4d22e5 команда хода (0x4d2260), 0x4d25d0 приход
 * (0x4d24b0), 0x4cc5ea, 0x4d248e - прочие (смысл - ранг 3); 0x4ccbf6 - перерисовка пути перед показом, не
 * пишется.
 */

namespace probe {

namespace {

constexpr std::uintptr_t selLayer = 0x838538;
constexpr std::uintptr_t pathLayer = 0x838590;

std::mutex marksMutex;

struct Flag
{
    int x;
    int y;
    std::string name;
};

/** Идёт показ пути (внутри 0x4ccc2c) - флажки копятся. */
thread_local bool inPath{};
thread_local std::vector<Flag> pathFlags;
thread_local std::string flagName;

/** Что видно сейчас: эллипс (клетка) и путь (сводка). */
bool selShown{};
int selX{-1};
int selY{-1};
bool pathShown{};
std::string pathLast;

std::string pointJson(const void* p)
{
    int xy[2]{};
    if (!p || !readable(p, sizeof(xy)))
        return "null";
    std::memcpy(xy, p, sizeof(xy));
    return fmt::format("[{},{}]", xy[0], xy[1]);
}

const char* clearVia(std::uintptr_t ret)
{
    switch (ret) {
    case 0x4d22e5:
        return "move";
    case 0x4d25d0:
        return "arrive";
    default:
        return "other";
    }
}

using ShowImage = void(__stdcall*)(const void* point, const void* layer, void* image, int a4, int a5);
ShowImage origShowImage{};

void __stdcall showImageProbe(const void* point, const void* layer, void* image, int a4, int a5)
{
    origShowImage(point, layer, image, a4, a5);
    const auto layerAddress = reinterpret_cast<std::uintptr_t>(layer);
    if (layerAddress != selLayer && layerAddress != pathLayer)
        return;
    int xy[2]{-1, -1};
    if (point && readable(point, sizeof(xy)))
        std::memcpy(xy, point, sizeof(xy));
    if (layerAddress == pathLayer) {
        if (inPath)
            pathFlags.push_back(Flag{xy[0], xy[1], flagName.empty() ? std::string{"?"} : flagName});
        flagName.clear();
        return;
    }
    const auto ret = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    {
        std::lock_guard<std::mutex> lock{marksMutex};
        if (selShown && selX == xy[0] && selY == xy[1])
            return;
        selShown = true;
        selX = xy[0];
        selY = xy[1];
    }
    countCall("map_sel");
    emit(nextSeq(), fmt::format(R"("ev":"map_sel","action":"show","x":{},"y":{},"via":"{}","ret_addr":"{}")",
                                xy[0], xy[1], ret == 0x4d2624 ? "arrive" : ret == 0x4cfb37 ? "select" : "other",
                                hexAddress(ret)));
}

using HideLayer = void(__stdcall*)(const void* layer);
HideLayer origHideLayer{};

void __stdcall hideLayerProbe(const void* layer)
{
    origHideLayer(layer);
    if (reinterpret_cast<std::uintptr_t>(layer) != selLayer)
        return;
    const auto ret = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    {
        std::lock_guard<std::mutex> lock{marksMutex};
        if (!selShown)
            return;
        selShown = false;
    }
    countCall("map_sel");
    emit(nextSeq(), fmt::format(R"("ev":"map_sel","action":"clear","x":null,"y":null,"via":"{}","ret_addr":"{}")",
                                ret == 0x4cfae0 ? "select" : ret == 0x4cf542 ? "city" : "other", hexAddress(ret)));
}

using FindImage = void*(__fastcall*)(void* images, int edx, const char* name, bool a2);
FindImage origFindImage{};

/** 0x5ae20c: картинка по имени - запомнить имя флажка пути (возврат 0x4cce7e). */
void* __fastcall findImageProbe(void* images, int edx, const char* name, bool a2)
{
    if (inPath && reinterpret_cast<std::uintptr_t>(_ReturnAddress()) == 0x4cce7e)
        flagName = readable(name, 1) ? jsonCString(name, 24) : std::string{"?"};
    return origFindImage(images, edx, name, a2);
}

/** Флажки сериями: «MOVENORMAL*3,MOVEOUT*2». */
std::string flagRuns(const std::vector<Flag>& flags)
{
    std::string out;
    for (std::size_t i = 0; i < flags.size();) {
        std::size_t j = i;
        while (j < flags.size() && flags[j].name == flags[i].name)
            ++j;
        out += fmt::format("{}{}*{}", out.empty() ? "" : ",", flags[i].name, j - i);
        i = j;
    }
    return out;
}

ShowPathFn origShowPath{};

/** Свой хук map_path на 0x4ccc2c - только когда хук "path" выключен (иначе метки зовёт он, см. showPathMarked). */
void __stdcall showPathProbe(const void* objectMap, const game::CMidgardID* stack, const void* path,
                             const void* lastReachable, const void* pathEnd, bool a6)
{
    showPathMarked(origShowPath, objectMap, stack, path, lastReachable, pathEnd, a6);
}

using ClearPath = void(__fastcall*)(void* self, int edx);
ClearPath origClearPath{};

void __fastcall clearPathProbe(void* self, int edx)
{
    origClearPath(self, edx);
    const auto ret = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    if (ret == 0x4ccbf6)
        return; // перерисовка перед показом (0x4ccbee)
    {
        std::lock_guard<std::mutex> lock{marksMutex};
        if (!pathShown)
            return;
        pathShown = false;
        pathLast.clear();
    }
    countCall("map_path");
    emit(nextSeq(), fmt::format(R"("ev":"map_path","action":"clear","via":"{}","ret_addr":"{}")", clearVia(ret),
                                hexAddress(ret)));
}

} // namespace

void showPathMarked(ShowPathFn show, const void* objectMap, const game::CMidgardID* stack, const void* path,
                    const void* lastReachable, const void* pathEnd, bool a6)
{
    const auto seq = nextSeq();
    const auto id = safeId(stack);
    std::uint32_t length = 0;
    readAt(path, 0, length);
    const auto reach = pointJson(lastReachable);
    const auto end = pointJson(pathEnd);
    pathFlags.clear();
    flagName.clear();
    {
        ScopedValue<bool> showing{inPath, true}; // и при исключении движка внутри показа
        show(objectMap, stack, path, lastReachable, pathEnd, a6);
    }
    const auto runs = flagRuns(pathFlags);
    const auto first = pathFlags.empty() ? std::string{"null"}
                                         : fmt::format("[{},{}]", pathFlags.front().x, pathFlags.front().y);
    const auto last = pathFlags.empty() ? std::string{"null"}
                                        : fmt::format("[{},{}]", pathFlags.back().x, pathFlags.back().y);
    const auto summary = fmt::format(R"("stack":{},"len":{},"flags":{},"names":"{}","first":{},"last":{},"reach":{},"end":{})",
                                     id, length, pathFlags.size(), runs, first, last, reach, end);
    pathFlags.clear();
    {
        std::lock_guard<std::mutex> lock{marksMutex};
        if (pathShown && pathLast == summary)
            return;
        pathShown = true;
        pathLast = summary;
    }
    countCall("map_path");
    emit(seq, fmt::format(R"("ev":"map_path","action":"show",{},"via":"show")", summary));
}

} // namespace probe

namespace hooks {

void addMapMarkHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    const GogHook entries[] = {
        {"map_sel", 0x5ad1ee, (void*)showImageProbe, (void**)&origShowImage},
        {"map_sel_clear", 0x5ad312, (void*)hideLayerProbe, (void**)&origHideLayer},
        {"map_path_clear", 0x4cd329, (void*)clearPathProbe, (void**)&origClearPath},
        {"map_path_img", 0x5ae20c, (void*)findImageProbe, (void**)&origFindImage},
    };
    const GogHook ownPath[] = {
        {"map_path", 0x4ccc2c, (void*)showPathProbe, (void**)&origShowPath},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
    // Ин130: 0x4ccc2c уже перехватывает хук "path" (probehooks.cpp), а второй DetourAttach на тот же адрес в одной транзакции
    // молча проигрывает - map_path тогда зовёт "path" (showPathMarked). Свой хук - только без "path".
    if (hookEnabled("path")) {
        if (gameVersion() == GameVersion::Gog && hookEnabled("map_path"))
            installed.push_back("map_path"); // зовёт хук "path" (showPathMarked)
    } else {
        addGogHooks(ownPath, hooks, installed);
    }
}

} // namespace hooks
