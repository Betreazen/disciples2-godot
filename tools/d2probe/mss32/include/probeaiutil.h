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

#ifndef PROBEAIUTIL_H
#define PROBEAIUTIL_H

/*
 * Схема 6.5: общие помощники probeaieco.cpp и probeaitac.cpp - безопасное чтение объектов ИИ,
 * списков движка и вызов виртуальных геттеров только по проверенной vftable.
 */

#include "mqpoint.h"
#include "probe.h"
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <fmt/format.h>
#include <string>

namespace probe::aiutil {

/** Границы кода Discipl2.exe (Gog 3.01): слот vftable вне их не зовётся. */
constexpr std::uintptr_t codeBegin = 0x401000;
constexpr std::uintptr_t codeEnd = 0x6cb000;

inline std::uintptr_t callerOf(void* returnAddress)
{
    return reinterpret_cast<std::uintptr_t>(returnAddress);
}

/** Id по адресу как JSON: строка в кавычках или null. */
inline std::string jsonId(const void* id)
{
    game::CMidgardID value{};
    return readAt(id, 0, value) ? fmt::format(R"("{}")", idToString(&value)) : std::string{"null"};
}

/** Id объекта сценария (vftable, id +4) как JSON. */
inline std::string objectId(const void* object)
{
    return object ? jsonId(static_cast<const char*>(object) + 4) : std::string{"null"};
}

inline std::string jsonDouble(bool known, double value)
{
    return known && std::isfinite(value) ? fmt::format("{}", value) : std::string{"null"};
}

inline std::string doubleAt(const void* base, std::size_t offset)
{
    double value = 0.0;
    const bool known = readAt(base, offset, value);
    return jsonDouble(known, value);
}

inline std::string intAt(const void* base, std::size_t offset)
{
    int value = 0;
    return readAt(base, offset, value) ? std::to_string(value) : std::string{"null"};
}

inline std::string byteAt(const void* base, std::size_t offset)
{
    std::uint8_t value = 0;
    return readAt(base, offset, value) ? std::to_string(value) : std::string{"null"};
}

inline std::string hexOf(std::uintptr_t value)
{
    return fmt::format(R"("0x{:x}")", value);
}

/** Сырые байты [offset, offset + size) как JSON-строка hex или null. */
inline std::string rawHex(const void* base, std::size_t offset, std::size_t size)
{
    if (!base || !readable(static_cast<const char*>(base) + offset, size))
        return {"null"};

    const auto* p = static_cast<const unsigned char*>(base) + offset;
    std::string out{"\""};
    for (std::size_t i = 0; i < size; ++i)
        out += fmt::format("{:02x}", p[i]);
    return out + '"';
}

inline std::string pointJson(const void* point)
{
    game::CMqPoint value{};
    return readAt(point, 0, value) ? fmt::format(R"({{"x":{},"y":{}}})", value.x, value.y)
                                   : std::string{"null"};
}

/** vftable объекта, если читается и сам слот 'slot' указывает в код exe; иначе 0. */
inline std::uintptr_t vslot(const void* object, std::size_t slot)
{
    std::uintptr_t vftable = 0;
    std::uintptr_t function = 0;
    if (!readAt(object, 0, vftable) || !readAt(reinterpret_cast<const void*>(vftable), slot * 4, function))
        return 0;
    return function >= codeBegin && function < codeEnd ? function : 0;
}

inline std::uintptr_t vftableOf(const void* object)
{
    std::uintptr_t vftable = 0;
    readAt(object, 0, vftable);
    return vftable;
}

/** Геттер без аргументов (thiscall) по слоту; ok = false, если слот не проверен. */
template <typename R>
R vget(const void* object, std::size_t slot, bool& ok)
{
    const auto function = vslot(object, slot);
    ok = function != 0;
    if (!ok)
        return R{};
    using Getter = R(__thiscall*)(const void*);
    return reinterpret_cast<Getter>(function)(object);
}

/**
 * Обход List<T> движка (length +0, head +4; узел: next +0, prev +4, данные +8) не дальше 'cap'
 * узлов. f(node) вызывается на каждый узел; false - прервать.
 */
template <typename F>
void walkList(const void* list, std::size_t cap, F f)
{
    const void* head = nullptr;
    if (!readAt(list, 4, head) || !head)
        return;

    const void* node = nullptr;
    if (!readAt(head, 0, node))
        return;

    for (std::size_t i = 0; i < cap && node && node != head; ++i) {
        if (!f(node))
            return;
        if (!readAt(node, 0, node))
            return;
    }
}

inline std::string listLength(const void* list)
{
    return intAt(list, 0);
}

} // namespace probe::aiutil

#endif // PROBEAIUTIL_H
