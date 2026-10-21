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
#include "utils.h"
#include "version.h"
#include <algorithm>
#include <cstring>
#include <filesystem>
#include <atomic>
#include <cstdint>
#include <fmt/format.h>
#include <fstream>
#include <memory>
#include <string>
#include <vector>
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

/*
 * Схема 7.3: покрытие кода Discipl2.exe за партию - какие функции исполнились хотя бы раз.
 *
 * Способ - одноразовые точки останова. Список начал функций (`probe/coverage.txt`, по строке
 * "0xADDR", `#` - комментарий; строка "# exe_size N" сверяется с размером exe) кладёт установщик
 * (`install.py --coverage`), сам список строит `tools/probe_coverage.py make-list` из процедур Ghidra и
 * vtable RTTI. armCoverage() делает секцию кода записываемой, запоминает первый байт каждой функции и
 * пишет туда 0xCC. Первое исполнение - EXCEPTION_BREAKPOINT; обработчик (VEH, раньше любого SEH игры)
 * находит адрес двоичным поиском, отмечает попадание (порядок, мс журнала, текущий seq), возвращает
 * байт и продолжает с того же адреса. Каждая функция - одно исключение за запуск (~2-5 мкс), дальше
 * она идёт без затрат: тысячи функций старта - доли секунды, игра не тормозит.
 *
 * Два потока на одной точке: оба попали на 0xCC до возврата байта - первый отмечает (CAS), оба
 * возвращают байт (повторная запись того же байта безвредна) и продолжают. Чужие int3 (не из списка)
 * обработчик не трогает - EXCEPTION_CONTINUE_SEARCH.
 *
 * Ставится ПОСЛЕ Detours (DllMain, setupHooks): переходник Detours - копия первых байт функции, и
 * 0xCC в момент копирования попал бы в копию. Начало функции под хуком - уже jmp Detours (0xE9),
 * точка останова встаёт на него и возвращает 0xE9. Байт 0xCC в списке (выравнивание) - пропуск.
 *
 * Выход - `<журнал>.cov.txt` рядом с журналом, переписывается в emitCounts() (смена дня, выгрузка) и
 * потоком dumpLoop() раз в минуту (падение игры выгрузки не даёт):
 * заголовок и строка на исполненную функцию "0xADDR порядок t seq" в порядке попадания. Свод по
 * областям и классам - `tools/probe_coverage.py report`.
 */

namespace probe {

namespace {

struct Coverage
{
    std::vector<std::uint32_t> address;            // по возрастанию
    std::vector<std::uint8_t> original;            // первый байт до точки останова
    std::unique_ptr<volatile LONG[]> hitOrder;     // 0 - не исполнялась, иначе порядок с 1
    std::unique_ptr<std::uint32_t[]> hitMs;        // 't' журнала при первом попадании
    std::unique_ptr<std::uint64_t[]> hitSeq;       // последний seq журнала до попадания
    volatile LONG hits{0};
    std::size_t skipped{0};                        // строки списка, не ставшие точкой
    std::size_t listed{0};
    bool armed{false};
};

Coverage coverage;
std::atomic_flag dumping = ATOMIC_FLAG_INIT;

/** Индекс адреса в списке или -1. */
std::ptrdiff_t findAddress(std::uint32_t address)
{
    const auto& list = coverage.address;
    const auto it = std::lower_bound(list.begin(), list.end(), address);
    if (it == list.end() || *it != address)
        return -1;

    return it - list.begin();
}

LONG CALLBACK coverageHandler(EXCEPTION_POINTERS* info)
{
    if (!info || !info->ExceptionRecord
        || info->ExceptionRecord->ExceptionCode != EXCEPTION_BREAKPOINT)
        return EXCEPTION_CONTINUE_SEARCH;

    const auto address = reinterpret_cast<std::uintptr_t>(info->ExceptionRecord->ExceptionAddress);
    const auto index = findAddress(static_cast<std::uint32_t>(address));
    if (index < 0)
        return EXCEPTION_CONTINUE_SEARCH;

    if (InterlockedCompareExchange(&coverage.hitOrder[index], -1, 0) == 0) {
        coverage.hitMs[index] = static_cast<std::uint32_t>(journalMs());
        coverage.hitSeq[index] = currentSeq();
        coverage.hitOrder[index] = InterlockedIncrement(&coverage.hits);
    }

    *reinterpret_cast<volatile std::uint8_t*>(address) = coverage.original[index];
    FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(address), 1);
    info->ContextRecord->Eip = static_cast<DWORD>(address);
    return EXCEPTION_CONTINUE_EXECUTION;
}

/** Секция кода exe: [начало, конец). */
bool textSection(std::uintptr_t& begin, std::uintptr_t& end)
{
    const auto module = reinterpret_cast<const std::uint8_t*>(GetModuleHandleA(nullptr));
    if (!module)
        return false;

    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(module);
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(module + dos->e_lfanew);
    const auto* section = IMAGE_FIRST_SECTION(nt);
    for (unsigned i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++section) {
        if (std::memcmp(section->Name, ".text", 5) == 0) {
            begin = reinterpret_cast<std::uintptr_t>(module) + section->VirtualAddress;
            end = begin + section->Misc.VirtualSize;
            return true;
        }
    }
    return false;
}

/** Адреса из probe/coverage.txt; false - файла нет или он для другого exe. */
bool readList(const std::filesystem::path& file, std::vector<std::uint32_t>& out)
{
    std::ifstream in{file};
    if (!in.is_open())
        return false;

    std::error_code ec;
    const auto size = std::filesystem::file_size(hooks::exePath(), ec);
    std::string line;
    while (std::getline(in, line)) {
        if (line.rfind("# exe_size ", 0) == 0) {
            if (ec || std::to_string(size) != hooks::trimSpaces(line.substr(11)))
                return false;
            continue;
        }
        if (line.empty() || line[0] == '#')
            continue;

        try {
            out.push_back(static_cast<std::uint32_t>(std::stoul(line, nullptr, 16)));
        } catch (const std::exception&) {
            ++coverage.skipped;
        }
    }
    return true;
}

constexpr DWORD dumpIntervalMs = 60'000;

/** Поток сброса: .cov.txt раз в минуту, чтобы падение игры теряло не больше минуты покрытия. */
DWORD WINAPI dumpLoop(LPVOID)
{
    for (;;) {
        Sleep(dumpIntervalMs);
        dumpCoverage();
    }
}

} // namespace

void armCoverage()
{
    if (!enabled() || !hooks::executableIsGame() || hooks::gameVersion() != hooks::GameVersion::Gog)
        return;

    std::vector<std::uint32_t> list;
    if (!readList(journalPath().parent_path() / "coverage.txt", list))
        return;

    std::uintptr_t begin = 0;
    std::uintptr_t end = 0;
    if (!textSection(begin, end))
        return;

    std::sort(list.begin(), list.end());
    list.erase(std::unique(list.begin(), list.end()), list.end());
    coverage.listed = list.size();

    DWORD oldProtect = 0;
    if (!VirtualProtect(reinterpret_cast<void*>(begin), end - begin, PAGE_EXECUTE_READWRITE,
                        &oldProtect))
        return;

    for (const auto address : list) {
        // Диапазон - до чтения: адрес вне секции кода в DllMain уронил бы игру при старте.
        if (address < begin || address >= end) {
            ++coverage.skipped;
            continue;
        }
        const auto byte = *reinterpret_cast<const std::uint8_t*>(address);
        if (byte == 0xcc) {
            ++coverage.skipped;
            continue;
        }
        coverage.address.push_back(address);
        coverage.original.push_back(byte);
    }

    const auto count = coverage.address.size();
    coverage.hitOrder = std::make_unique<volatile LONG[]>(count);
    coverage.hitMs = std::make_unique<std::uint32_t[]>(count);
    coverage.hitSeq = std::make_unique<std::uint64_t[]>(count);

    // Обработчик - до первой точки: иначе первая же исполненная функция уронила бы игру.
    if (!AddVectoredExceptionHandler(1, coverageHandler))
        return;

    for (const auto address : coverage.address)
        *reinterpret_cast<volatile std::uint8_t*>(address) = 0xcc;
    FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(begin), end - begin);
    coverage.armed = true;

    // Смены дня и выгрузки мало: 19.10 игра упала на ходу ИИ (0x58AE84, её обработчик закрыл процесс без
    // DLL_PROCESS_DETACH), и .cov.txt остался на смене дня часом раньше. CreateThread, а не std::thread: мы в
    // DllMain, поток стартует после снятия замка загрузчика, и ждать его здесь нельзя.
    if (const HANDLE thread = CreateThread(nullptr, 0, dumpLoop, nullptr, 0, nullptr))
        CloseHandle(thread);

    emit(nextSeq(), fmt::format(R"("ev":"coverage","armed":{},"listed":{},"skipped":{},"file":"{}")",
                                count, coverage.listed, coverage.skipped,
                                jsonCString(journalPath().filename().replace_extension(".cov.txt")
                                                .string().c_str(),
                                            260)));
}

std::string dumpCoverage()
{
    if (!coverage.armed || dumping.test_and_set())
        return {};

    const auto count = coverage.address.size();
    std::vector<std::size_t> hit;
    hit.reserve(static_cast<std::size_t>(coverage.hits));
    for (std::size_t i = 0; i < count; ++i) {
        if (coverage.hitOrder[i] > 0)
            hit.push_back(i);
    }
    std::sort(hit.begin(), hit.end(), [](std::size_t a, std::size_t b) {
        return coverage.hitOrder[a] < coverage.hitOrder[b];
    });

    std::string text = fmt::format("# coverage {} armed {} hit {} t {}\n# address order t seq\n",
                                   journalPath().filename().string(), count, hit.size(),
                                   journalMs());
    for (const auto i : hit) {
        text += fmt::format("0x{:x} {} {} {}\n", coverage.address[i], coverage.hitOrder[i],
                            coverage.hitMs[i], coverage.hitSeq[i]);
    }

    auto file = journalPath();
    file.replace_extension(".cov.txt");
    auto temp = file;
    temp += L".tmp";
    // Во временный файл и заменой: падение посреди записи не оставит усечённый .cov.txt.
    const HANDLE handle = CreateFileW(temp.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                                      CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    bool written = false;
    if (handle != INVALID_HANDLE_VALUE) {
        DWORD done = 0;
        written = WriteFile(handle, text.data(), static_cast<DWORD>(text.size()), &done, nullptr)
                  && done == text.size();
        CloseHandle(handle);
        written = written
                  && MoveFileExW(temp.c_str(), file.c_str(), MOVEFILE_REPLACE_EXISTING) != FALSE;
    }

    dumping.clear();
    return fmt::format(R"(,"coverage":{{"armed":{},"hit":{},"written":{}}})", count, hit.size(),
                       written);
}

} // namespace probe
