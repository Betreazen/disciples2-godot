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
#include <fmt/format.h>
#include <string>
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

/*
 * The input journal, corpus schema v5: what the player pressed and where the cursor was at that
 * moment, so a hand-played run can be put next to the engine events and next to the saves.
 *
 * Why the message pump and not the window procedure or a hook chain: PeekMessageA / GetMessageA
 * are plain user32 exports, so their addresses hold for every build of the game and need no
 * reverse engineering at all, while the window procedure would have to be found first and a
 * WH_KEYBOARD_LL hook would need a thread of its own. Every message the game acts on passes
 * through here.
 *
 * Not a flood: a line is written only for a button or a key going down (plus the wheel), never
 * for mouse movement, and never for the key repeat the keyboard driver generates while a key is
 * held. Everything else returns without touching a lock. How often the pump itself ran is in
 * "input_pump" of the 'counts' line - the unbudgeted counter that keeps "the player pressed
 * nothing" apart from "the game never calls the function we hooked".
 *
 * The map tile under the cursor is deliberately NOT computed here: the engine already publishes
 * it in the 'path' event on every hover (end_x / end_y), so the nearest preceding 'path' line
 * answers the question without a screen-to-tile conversion of our own.
 */

namespace probe {

namespace {

using PeekMessageAFn = BOOL(WINAPI*)(LPMSG, HWND, UINT, UINT, UINT);
using GetMessageAFn = BOOL(WINAPI*)(LPMSG, HWND, UINT, UINT);

PeekMessageAFn origPeekMessageA{};
GetMessageAFn origGetMessageA{};

/** Absent values are journaled as this, never as a plausible coordinate. */
constexpr int notComputed = -1;

/** Short name of the message, or nullptr for everything the journal stays silent about. */
const char* inputKind(const MSG* message)
{
    switch (message->message) {
    case WM_LBUTTONDOWN:
    case WM_RBUTTONDOWN:
    case WM_MBUTTONDOWN:
    case WM_XBUTTONDOWN:
        return "down";
    case WM_LBUTTONDBLCLK:
    case WM_RBUTTONDBLCLK:
    case WM_MBUTTONDBLCLK:
        return "dblclk";
    case WM_MOUSEWHEEL:
        return "wheel";
    case WM_KEYDOWN:
    case WM_SYSKEYDOWN:
        // Bit 30 of lParam is the previous key state: set means the driver is repeating a key
        // that is already held. One press must give one line.
        return (message->lParam & (1 << 30)) ? nullptr : "key";
    default:
        return nullptr;
    }
}

/** Which button the message is about; -1 for a keyboard message. */
int buttonOf(UINT message)
{
    switch (message) {
    case WM_LBUTTONDOWN:
    case WM_LBUTTONDBLCLK:
        return 1;
    case WM_RBUTTONDOWN:
    case WM_RBUTTONDBLCLK:
        return 2;
    case WM_MBUTTONDOWN:
    case WM_MBUTTONDBLCLK:
        return 3;
    case WM_XBUTTONDOWN:
        return 4;
    default:
        return notComputed;
    }
}

/**
 * Writes one 'input' line. 'x'/'y' are client coordinates of the window the message went to -
 * that is the coordinate system the game draws in - and 'sx'/'sy' are the screen coordinates the
 * message itself carries, kept because a click can land on a window whose client area we cannot
 * resolve.
 */
void emitInput(const MSG* message)
{
    const char* kind = message ? inputKind(message) : nullptr;
    if (!kind)
        return;

    POINT client = message->pt;
    const bool haveClient = message->hwnd && ScreenToClient(message->hwnd, &client);

    const auto seq = nextSeq();
    countCall("input");
    emit(seq,
         fmt::format(
             R"("ev":"input","kind":"{}","msg":{},"vk":{},"button":{},"x":{},"y":{},"sx":{},"sy":{})",
             kind, message->message,
             (kind[0] == 'k') ? static_cast<int>(message->wParam) : notComputed,
             buttonOf(message->message), haveClient ? client.x : notComputed,
             haveClient ? client.y : notComputed, message->pt.x, message->pt.y));

    // Бит 30 lParam - клавиша уже была нажата (автоповтор): один снимок на одно нажатие.
    constexpr LPARAM repeatBit = 1 << 30;
    if (message->message == WM_KEYDOWN && message->wParam == VK_F12 && !(message->lParam & repeatBit))
        noteScreenshotKey();
}

BOOL WINAPI peekMessageAProbe(LPMSG message, HWND window, UINT first, UINT last, UINT removeMsg)
{
    countPump();
    const BOOL result = origPeekMessageA(message, window, first, last, removeMsg);

    // Only the removing peek: with PM_NOREMOVE the same message is returned over and over until
    // somebody takes it, and one press would give a run of identical lines.
    if (result && (removeMsg & PM_REMOVE))
        emitInput(message);

    return result;
}

BOOL WINAPI getMessageAProbe(LPMSG message, HWND window, UINT first, UINT last)
{
    countPump();
    const BOOL result = origGetMessageA(message, window, first, last);

    // GetMessage always removes the message; -1 is its error return, and message is untouched.
    if (result > 0)
        emitInput(message);

    return result;
}

/** Address of a user32 export, or nullptr when it cannot be resolved. */
void* user32Export(const char* name)
{
    HMODULE user32 = GetModuleHandleA("user32.dll");
    return user32 ? reinterpret_cast<void*>(GetProcAddress(user32, name)) : nullptr;
}

} // namespace

} // namespace probe

namespace hooks {

void addInputHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    struct Entry
    {
        const char* name;
        const char* exportName;
        void* hook;
        void** original;
    };

    const Entry entries[] = {
        {"input", "PeekMessageA", (void*)probe::peekMessageAProbe, (void**)&probe::origPeekMessageA},
        {"input_get", "GetMessageA", (void*)probe::getMessageAProbe, (void**)&probe::origGetMessageA},
    };

    for (const auto& entry : entries) {
        void* target = probe::user32Export(entry.exportName);
        if (!target || !probe::hookEnabled(entry.name))
            continue;

        hooks.push_back({target, entry.hook, entry.original});
        installed.push_back(entry.name);
    }
}

} // namespace hooks
