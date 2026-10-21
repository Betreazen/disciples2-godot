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

#include "gamesettings.h"
#include "mapgraphics.h"
#include "midgard.h"
#include "mqpoint.h"
#include "probe.h"
#include "smartptr.h"
#include <fmt/format.h>
#include <cstdint>
#include <string>
#include <vector>
#include <windows.h>

/*
 * Схема 7.1: снимок экрана клавишей F12 - screenshot.
 *
 * Под обёрткой DisciplesGL F12 до движка не доходит: её оконная процедура глотает VK_F12 всегда
 * (DisciplesGL-HD Window.cpp: `wParam == VK_F12 -> return NULL`), а свою клавишу снимка (по
 * умолчанию та же F12) обрабатывает сама - пишет кадр в тот же ScreenShots\ScreenShotNNN.png|bmp на
 * следующей отрисовке. Поэтому строка пишется двумя путями: "via":"key" - нажатие F12 из хука ввода
 * (probeinput.cpp, клиентский поток, до обработки сообщения; имени файла ещё нет - "file":null, файл
 * находится по "time_ms"), "via":"engine" - снимок самого движка (без обёртки).
 *
 * 0x5aaa38 - void __cdecl(), ret: движок пишет ScreenShots\ScreenShotNNN.bmp (первый свободный
 * номер, строки 0x6eb6a4 'ScreenShot%03d', 0x6eb6b4 'ScreenShot???.bmp'). После оригинала зонд
 * пишет имя самого нового файла по этой маске, клетку в центре вида карты и настройки экрана -
 * по ним сверка карты со снимком (docs/ai-screen.md §14) ставит камеру без догадок.
 *
 * Клетка центра - 0x540b6c __thiscall(MapGraphics** данные MapGraphicsPtr, CMqPoint* out,
 * int* dx, int* dy), ret 0xc; вызов - как у движка (0x4f47ef: getMapGraphics 0x5404d9, ecx =
 * [ptr+4]). dx/dy - необязательные выходы той же функции, пишутся как есть. Вне карты данных нет -
 * "camera":null.
 */

namespace probe {

namespace {

using namespace game;

using ScreenShot = void(__cdecl*)();
ScreenShot origScreenShot{};

using CenterCell = void(__thiscall*)(MapGraphics** thisptr, CMqPoint* cell, int* dx, int* dy);
const auto centerCell = reinterpret_cast<CenterCell>(0x540b6c);

/** Самый новый ScreenShots\ScreenShot???.bmp (по времени записи) как JSON-строка или null. */
std::string newestShot()
{
    WIN32_FIND_DATAA data{};
    const HANDLE find = FindFirstFileA("ScreenShots\\ScreenShot???.bmp", &data);
    if (find == INVALID_HANDLE_VALUE)
        return {"null"};

    std::string best;
    FILETIME bestTime{};
    do {
        if (best.empty() || CompareFileTime(&data.ftLastWriteTime, &bestTime) >= 0) {
            bestTime = data.ftLastWriteTime;
            best = data.cFileName;
        }
    } while (FindNextFileA(find, &data));
    FindClose(find);
    return fmt::format(R"("{}")", best);
}

/** Клетка в центре вида карты {x,y,dx,dy} или null вне карты. */
std::string cameraJson()
{
    MapGraphicsPtr graphics{};
    MapGraphicsApi::get().getMapGraphics(&graphics);
    std::string result{"null"};
    if (graphics.data && *graphics.data) {
        CMqPoint cell{};
        int dx = 0;
        int dy = 0;
        centerCell(graphics.data, &cell, &dx, &dy);
        result = fmt::format(R"({{"x":{},"y":{},"dx":{},"dy":{}}})", cell.x, cell.y, dx, dy);
    }
    SmartPointerApi::get().createOrFree(reinterpret_cast<SmartPointer*>(&graphics), nullptr);
    return result;
}

/** Размер экрана и настройки, меняющие картинку, - "display":{...} или null. */
std::string displayJson()
{
    const auto* midgard = CMidgardApi::get().instance();
    if (!midgard || !midgard->data || !midgard->data->settings || !*midgard->data->settings)
        return {"null"};

    const auto* settings = *midgard->data->settings;
    return fmt::format(
        R"({{"width":{},"height":{},"use_d3d":{},"fullscreen":{},"animated_iso":{},"iso_anim":{},"flipped_battle":{},"turn_summary":{}}})",
        settings->displayWidth, settings->displayHeight, settings->useD3d,
        settings->fullscreenDisplayMode, settings->animatedIso, settings->playIsoAnim,
        settings->flippedBattle, settings->showTurnSummary);
}

/** Время по часам Windows, мс от 1970 - сверка с временем записи файла снимка. */
long long unixMs()
{
    FILETIME now{};
    GetSystemTimeAsFileTime(&now);
    const auto ticks = (static_cast<unsigned long long>(now.dwHighDateTime) << 32) | now.dwLowDateTime;
    constexpr unsigned long long epochTicks = 116444736000000000ULL; // 1601 -> 1970, по 100 нс
    return static_cast<long long>((ticks - epochTicks) / 10000ULL);
}

void emitScreenshot(std::uint64_t seq, const char* via, const std::string& file)
{
    countCall("screenshot");
    emit(seq, fmt::format(
                  R"("ev":"screenshot","via":"{}","file":{},"time_ms":{},"camera":{},"display":{},"ai_player":{},{})",
                  via, file, unixMs(), cameraJson(), displayJson(), aiTurnPlayer(), screenFields()));
}

void __cdecl screenShotProbe()
{
    const auto seq = nextSeq();
    origScreenShot();
    emitScreenshot(seq, "engine", newestShot());
}

} // namespace

void noteScreenshotKey()
{
    emitScreenshot(nextSeq(), "key", "null");
}

} // namespace probe

namespace hooks {

void addShotHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    // clang-format off
    const GogHook entries[] = {
        {"screenshot", 0x5aaa38, (void*)screenShotProbe, (void**)&origScreenShot},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
}

} // namespace hooks
