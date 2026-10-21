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

#ifndef PROBE_H
#define PROBE_H

#include "hooks.h"
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace game {
struct IMidgardObjectMap;
struct Bank;
struct CMidStack;
struct CMidPlayer;
struct CMidUnit;
struct IBatAttack;
} // namespace game

namespace probe {

/**
 * Opens the probe journal if the game folder contains a 'probe' subfolder.
 * Must be called after the game version is determined and before hooks are set up.
 * Also reads the probe/hooks.txt hook whitelist and 'toolset:' directives, if present (see
 * hookEnabled() and toolsetDirectives()).
 */
void init(const std::filesystem::path& gameDir);

/** Returns true if the journal is open, i.e. the game runs in probe mode. */
bool enabled();

/**
 * True if the hook named 'name' should be installed: always true when the game folder has no
 * probe/hooks.txt file, otherwise true only when 'name' is listed in it. An existing but empty
 * file disables every hook, leaving only the 'start' line - the single-log-line baseline used to
 * bisect which hook causes a behaviour difference.
 */
bool hookEnabled(const std::string& name);

/**
 * True only if probe/hooks.txt exists and explicitly lists 'name' as a hook to install - unlike
 * hookEnabled(), an absent file does NOT enable it. For hooks that must default to off (currently
 * only 'menu_force_main': an experiment, not part of the always-on probe hook set).
 */
bool hookExplicitlyEnabled(const std::string& name);

/** Toolset bisection directives parsed from probe/hooks.txt by init(). Default (all false) when
 *  hooks.txt is absent or carries none of them. */
const hooks::ToolsetDirectives& toolsetDirectives();

/**
 * Writes the 'start' journal line, including the names of the hooks getProbeHooks() actually
 * installed (after the probe/hooks.txt filter and the null-target check), plus how many extra
 * toolset hooks the 'toolset:' directives above added ('toolsetCount') and which
 * getGameHooks() indices were skipped as address conflicts with a probe hook ('toolsetSkipped').
 * Called once, from getProbeHooks(), right before its hook table is committed - no hook can fire
 * before that, so 'start' stays the first line in the journal.
 */
void emitStart(const std::vector<std::string>& hookNames,
              std::size_t toolsetCount,
              const std::vector<std::size_t>& toolsetSkipped);

/**
 * Returns true if the game folder contains a 'probe/autobattle' marker file. The probe then turns
 * auto and fast battle on for every battle it sees, so an unattended run does not wait for a human.
 * This is the only thing the probe changes in the game; the formulas stay vanilla.
 */
bool autoBattleForced();

/**
 * Marks whether a battle is currently in progress: set true when battle_start is written
 * (beforeBattleRound, first call for a BattleMsgData) and false right after battle_end is written
 * (updateGroupsIfBattleIsOver) - i.e. the window is [battle_start, battle_end] inclusive. Gates
 * whether emitRng() actually writes the rng / rng_std line: a full run is ~50k of those, nearly
 * all outside battle (AI planning, income, ...), and only a couple dozen matter for a battle-shaped
 * journal. The rng/rng_std counters themselves keep advancing regardless, so rng_before/rng_after
 * on miss/dmg stay meaningful.
 */
void setBattleActive(bool active);

/** Схема 6.0, партия 2: текущее значение флага setBattleActive - идёт ли бой. */
bool battleActive();

/**
 * Returns the next event sequence number. Taken before the original call, so nested events
 * (computeDamage calls computeArmor and friends) can be put back in call order.
 */
std::uint64_t nextSeq();

/** Writes one journal line: {"seq":..,"tid":..,<fields>}. Thread safe. */
void emit(std::uint64_t seq, std::string_view fields);

/**
 * Counts one call of 'event' without writing anything. The counter has no budget and no cap: it
 * is the half of keyed logging that keeps "no line" and "never called" distinguishable. Dumped by
 * emitCounts().
 */
void countCall(std::string_view event);

/** Схема 7.3: последний выданный nextSeq() - без нового номера (первое попадание в функцию для покрытия). */
std::uint64_t currentSeq();

/** Схема 7.3: миллисекунды с открытия журнала - то же 't', что у строк. */
std::uint64_t journalMs();

/** Схема 7.3: путь открытого журнала (пусто без зонда) - рядом с ним пишется файл покрытия. */
const std::filesystem::path& journalPath();

/**
 * Схема 7.3: покрытие кода Discipl2.exe. Включается файлом probe/coverage.txt (список адресов начал
 * функций, `tools/probe_coverage.py make-list`): на каждое начало ставится одноразовая точка
 * останова (0xCC), обработчик исключений (VEH) отмечает первое попадание - порядок, 't', 'seq' - и
 * возвращает исходный байт, так что каждая функция стоит одно исключение за весь запуск. Зовётся из
 * DllMain после установки хуков: Detours копирует первые байты функций в свои переходники, и точка
 * останова до него попала бы в копию. Без файла, вне игры и не на Gog - ничего не делает.
 * Defined in probecov.cpp.
 */
void armCoverage();

/**
 * Схема 7.3: переписывает <журнал>.cov.txt (адрес, порядок, 't', 'seq' каждой исполненной функции) и
 * возвращает поле ',"coverage":{...}' для строки counts (пусто без покрытия). Зовёт emitCounts() - на
 * смене дня и при выгрузке DLL; без замков: только чтение массивов и запись файла. probecov.cpp.
 */
std::string dumpCoverage();

/** Схема 7.3: дописывает затухания музыки, кончившиеся без следующего события (смена дня). probeav.cpp. */
void flushMusicRamps();

/** Схема 7.3: дописывает незаконченную серию прокрутки вида (строка view). probeanim.cpp. */
void flushViewRun();

/**
 * Keyed logging. Pathfinding calls computeMovementCost thousands of times per mouse move, so
 * writing every call is useless, and a first-N budget is forbidden by the project rule: the
 * frequent case eats the budget and hides the rare one. Instead a line is written once per new
 * feature combination ('key'), while countCall() records how often the function really ran.
 * Thread safe.
 */
void emitKeyed(std::uint64_t seq,
               std::string_view event,
               std::string_view key,
               std::string_view fields);

/**
 * Схема 7.4: забывает все ключи emitKeyed() - следующая строка каждого сочетания снова пишется.
 * Зовётся при входе в партию (хук game_enter, probeui.cpp, сразу после строки game_phase), чтобы
 * длинный прогон из нескольких карт не исчерпал потолки на первой. Счётчики calls и отброшенные
 * (keys_dropped, keys_dropped_by) не сбрасываются - они за весь прогон; сбросы считает keys_resets.
 * Thread safe.
 */
void resetKeys();

/**
 * Writes one "counts" line carrying every countCall() counter so far. Called when the day changes
 * (income hook) and once more from DllMain's DLL_PROCESS_DETACH, so a journal ends with the real
 * call totals even when no day ever changed.
 */
void emitCounts();

/** Writes an rng / rng_std event, bumping the matching counter under the journal lock. */
void emitRng(std::uint64_t seq, bool isStd, unsigned int maxValue, int result);

/**
 * Counts one call of the window message pump (probeinput.cpp). Lock free and allocation free on
 * purpose: PeekMessage runs on every pass of the render loop, so the ordinary countCall(), with
 * its mutex and its map lookup, would sit in the hottest path of the game. Reported by
 * emitCounts() as "input_pump" next to "keys_dropped": it is what tells "the pump hook stood
 * there and the player pressed nothing" apart from "the game does not call the function we
 * hooked at all". Corpus schema v5.
 */
void countPump() noexcept;

/**
 * Writes the 'fort_day' line: every fortification owned by 'playerId', with its level, the day of
 * its last growth and its garrison. Called from the income hook right after 'terrain_day', for
 * the same reason as that one: computePlayerDailyIncome runs exactly once per player per day and
 * already holds the object map, so no new engine address is needed. Corpus schema v5.
 */
void emitFortDay(const game::IMidgardObjectMap* objectMap,
                 const game::CMidgardID* playerId,
                 int turn);

/**
 * True when 'size' bytes at 'p' can be read (VirtualQuery). Every pointer a hook knows only by
 * position goes through here before it is dereferenced: a wrong position must give "null" in the
 * journal, never a crash of a live run. Defined in probeecon.cpp.
 */
bool readable(const void* p, std::size_t size);

/** Id at 'p' as a string, or "null" when nothing can be read there. Defined in probeecon.cpp. */
std::string safeId(const void* p);

/**
 * Схема 6.0: копирует sizeof(T) байт по адресу base + offset в 'out', если они читаются
 * (readable). Поля эффектов карты известны только по смещениям из дизассемблирования, и неверное
 * смещение должно давать "null"/-1 в журнале, а не падение живого прогона. При false 'out' не
 * тронут - начальное значение и есть «не прочиталось».
 */
template <typename T>
bool readAt(const void* base, std::size_t offset, T& out)
{
    if (!base)
        return false;

    const auto* p = static_cast<const char*>(base) + offset;
    if (!readable(p, sizeof(T)))
        return false;

    std::memcpy(&out, p, sizeof(T));
    return true;
}

/**
 * Схема 7.3: значение потоковой переменной на время вызова оригинала, с возвратом прежнего и при выходе
 * исключением (SEH/C++ движка) - иначе указатель на кадр стека пережил бы вызов.
 */
template <typename T>
class ScopedValue
{
public:
    ScopedValue(T& slot, T value)
        : slot_(slot)
        , saved_(slot)
    {
        slot_ = value;
    }
    ~ScopedValue()
    {
        slot_ = saved_;
    }
    ScopedValue(const ScopedValue&) = delete;
    ScopedValue& operator=(const ScopedValue&) = delete;

private:
    T& slot_;
    T saved_;
};

/** True, если читаются и объект карты, и его vftable: проверка перед любым вызовом движка через карту. */
bool readableMap(const game::IMidgardObjectMap* objectMap);

/** Отряд по id или nullptr, если карту, id или сам отряд прочитать нельзя. Defined in probeecon.cpp. */
const game::CMidStack* readableStack(const game::IMidgardObjectMap* objectMap,
                                     const game::CMidgardID* stackId);

/** Игрок по id или nullptr - то же правило. Defined in probeecon.cpp. */
const game::CMidPlayer* readablePlayer(const game::IMidgardObjectMap* objectMap,
                                       const game::CMidgardID* playerId);

/** Юнит по id (findUnitById) или nullptr - то же правило. Схема 6.0, партия 2; probeecon.cpp. */
const game::CMidUnit* readableUnit(const game::IMidgardObjectMap* objectMap,
                                   const game::CMidgardID* unitId);

/**
 * Тип предмета (CMidItem::globalItemId, +8) как готовое JSON-значение: строка в кавычках или null
 * (пустой слот, id не предмета, объект не прочитан) - подставлять в формат без кавычек.
 * Defined in probecheck.cpp.
 */
std::string itemTypeOf(const game::IMidgardObjectMap* objectMap, const game::CMidgardID* itemId);

/**
 * Ид-поля события, названные по типу id ("site", "stack", ..., повтор или незнакомый тип - "idN"),
 * общее правило схемы 5. Defined in probeecon.cpp.
 */
std::string idsField(const void* const* args, std::size_t count);

/** Раса игрока (LRaceCategory::id) или -1. Defined in probediplo.cpp. */
int playerRace(const game::IMidgardObjectMap* objectMap, const game::CMidgardID* playerId);

/**
 * Пара рас двух игроков из CMidDiplomacy - {"cur","prev","ally","eternal_war","raw"}, как before/after
 * события relation, - или null, если пары нет. Defined in probediplo.cpp.
 */
std::string playerPairRelation(const game::IMidgardObjectMap* objectMap,
                               const game::CMidgardID* playerA,
                               const game::CMidgardID* playerB);

/**
 * Слово отношений пары рас двух игроков (CMidDiplomacy, как у playerPairRelation) в 'word'; false -
 * пары нет или карту не прочитать. Схема 6.4. Defined in probediplo.cpp.
 */
bool playerPairWord(const game::IMidgardObjectMap* objectMap,
                    const game::CMidgardID* playerA,
                    const game::CMidgardID* playerB,
                    std::uint32_t& word);

/** "0x5da03c": адрес в том виде, в каком его пишут ret_addr и effect. Defined in probe.cpp. */
std::string hexAddress(std::uintptr_t address);

/**
 * Казна как JSON-объект {gold, infernal, life, death, runic, grove} - тот же вид, что у событий
 * bank и income. Defined in probeecon.cpp.
 */
std::string bankObject(const game::Bank& bank);

/** Returns id as string, same format as hooks::idToString; "null" for a null id. */
std::string idToString(const game::CMidgardID* id);

/** Number of generateRandomNumber calls recorded so far. generateRandomNumberStd is separate. */
std::uint64_t rngCount();

/** Схема 6.5: число вызовов generateRandomNumberStd (строки rng_std пишутся только в бою). */
std::uint64_t rngStdCount();

/**
 * Схемы 6.5 и 7.5: строка ai_rng, если бросок пришёл из решения игры вне показа и боя (места -
 * probeairng.cpp); иначе ничего. Зовут хуки rng / rng_std после emitRng; returnSlot - адрес слота
 * адреса возврата (_AddressOfReturnAddress хука) для цепочки обёрток outer.
 */
void tagAiRng(std::uint64_t seq,
              std::uintptr_t returnAddress,
              const void* returnSlot,
              bool isStd,
              unsigned int maxValue,
              int result);

/**
 * Схема 6.5: сырое значение последнего msvcrt!rand этого потока, если он позвался после строки с
 * номером seq (внутри того же вызова обёртки). Defined in probeaieco.cpp.
 */
bool rawRandAfter(std::uint64_t seq, int& value);

/** Схема 6.5: игрок, чей ход ИИ начался последним, как JSON (или null). Defined in probeai.cpp. */
std::string aiTurnPlayer();

/** Схема 6.5: номер текущего прохода стратегии в ходе (исполненных проходов + 1). probeai.cpp. */
long long aiPass();

/**
 * Значение, которое generateRandomNumber вернул на вызове номер 'n' (нумерация та же, что у
 * rngCount()), или -1, если этот вызов уже вытеснен из кольца последних четырёх.
 *
 * Нужно событиям вне боя: строка `rng` пишется, только пока бой активен (setBattleActive), а
 * бросок вора идёт по карте приключений - до появления кольца сами кубики в журнал не попадали.
 * Спрашивать можно только про вызовы, случившиеся прямо сейчас: сразу после возврата из обёртки,
 * по номерам rng_before+1 и rng_before+2.
 */
int rngResultAt(std::uint64_t n);

/**
 * "rng_index":N JSON fragment with a leading comma, or an empty string when the value cannot be
 * trusted: only the Gog build (hooks::gameVersion()) has the R250 index address (0x838c24) and
 * table (0x7b4978) confirmed - see docs/re-rng.md in disciples-modding. N is the index as it
 * stands at the moment of the call: the state the *next* generateRandomNumber draw will start
 * from. Plain volatile read, no critical section (0x836fd8) - an aligned dword read is atomic on
 * x86, and the battle runs on the engine's main thread only, so there is nothing to race with.
 */
std::string rngIndexField();

/**
 * Схема 6.1: запоминает боевое действие, созданное createBatAttack (строка attack_new с номером
 * 'seq'), чтобы attack_hit мог назвать, кто бьёт и какой атакой. Кольцо на 64 записи под своим
 * замком. Defined in probehit.cpp.
 */
void rememberBatAttack(const game::IBatAttack* object,
                       std::uint64_t seq,
                       const std::string& attacker,
                       const std::string& attack);

/**
 * C-строка игры (cp1251) как тело JSON-строки в UTF-8, не длиннее maxLength байт исходника; у края
 * читаемой памяти обрывается. Defined in probemap.cpp.
 */
std::string jsonCString(const char* text, std::size_t maxLength);

/**
 * Схема 7.0: id текста, разрешённый getInterfaceText, - строка ui_text по ключу (экран, id) и
 * «последний текст» для ui_msgbox. Зовётся из хука ui_refusal (probeunit.cpp). Defined in
 * probeui.cpp.
 */
void noteInterfaceText(const game::CMidgardID* textId);

/** Схема 7.1: поля "top","top_class" верхнего экрана (как у строк ui_*). Defined in probeui.cpp. */
std::string screenFields();

/**
 * Схема 7.1: нажата F12 (WM_KEYDOWN без повтора) - строка screenshot "via":"key" с клеткой камеры.
 * Зовёт хук ввода в клиентском потоке до обработки сообщения. Defined in probeshot.cpp.
 */
void noteScreenshotKey();

/**
 * Схема 7.1, часть 3: класс объекта по RTTI (vftable[-1] -> COL -> TypeDescriptor, без ".?AV" и
 * "@@") - "null" без vftable, "?" без RTTI. Тот же кеш, что у ui_*. Defined in probeui.cpp.
 */
std::string rttiClassName(const void* object);

/**
 * Схема 7.1, часть 3: строка rng_state - состояние R250, с которым открывается сценарий, и
 * состояние, записанное в заголовке сохранения. Зовёт хук game_start (0x429603) в серверном
 * потоке ДО оригинала; 'server' - его this. Defined in probestate.cpp.
 */
void noteScenarioOpen(const void* server, bool newGame, std::uintptr_t returnAddress);

/** ShowMovementPath 0x4ccc2c (game.h), указатели без типов движка. */
using ShowPathFn = void(__stdcall*)(const void* objectMap,
                                    const game::CMidgardID* stack,
                                    const void* path,
                                    const void* lastReachable,
                                    const void* pathEnd,
                                    bool a6);

/**
 * Схема 7.6, map_path: показ пути 'show' (оригинал ShowMovementPath) с подсчётом флажков и строкой
 * map_path на смену видимого. Хук "path" (probehooks.cpp) стоит на том же адресе 0x4ccc2c, а Detours
 * оставляет один перехват на адрес - поэтому при включённом "path" метки зовёт он, а свой хук
 * map_path не ставится (до Ин130 строка map_path 7.6 не писалась ни разу). Только Gog. Проверено
 * компилятором; на живой игре - с первой партией после сборки. Defined in probemarks.cpp.
 */
void showPathMarked(ShowPathFn show,
                    const void* objectMap,
                    const game::CMidgardID* stack,
                    const void* path,
                    const void* lastReachable,
                    const void* pathEnd,
                    bool a6);

} // namespace probe

namespace hooks {

/** Returns the probe-only hooks: each one calls the original and writes a journal line. */
Hooks getProbeHooks();

/**
 * Appends the corpus schema v5 hooks - treasury, map objects, items, city, thief - to 'hooks' and
 * their names to 'installed'. Defined in probeecon.cpp, which also keeps their addresses: they
 * are confirmed for the Gog build only and are deliberately NOT put into the positional
 * game.cpp version table, where a row off by one silently binds a hook to the wrong function.
 * Called only for the game executable, never for the scenario editor.
 */
void addEconHooks(Hooks& hooks, std::vector<std::string>& installed);

/**
 * Appends the input journal hooks (window message pump) to 'hooks'. Defined in probeinput.cpp.
 * These are user32 exports, not engine addresses, so they hold for every build of the game.
 */
void addInputHooks(Hooks& hooks, std::vector<std::string>& installed);

/**
 * Добавляет хук схемы 5.2 - мутатор дипломатических отношений `relation` (0x5d9366). Определён в
 * probediplo.cpp вместе со своим адресом и таблицей «адрес возврата -> причина»; адрес
 * подтверждён только на Gog, на прочих сборках хук не ставится. Зовётся только для игры.
 */
void addDiploHooks(Hooks& hooks, std::vector<std::string>& installed);

/**
 * Добавляет хуки схемы 5.3 - события-причины трат казны: найм юнита и лидера, постановка жезла,
 * применение заклинания, изучение заклинания, посох или свиток. Определён в probespend.cpp вместе
 * с адресами; адреса подтверждены только на Gog, на прочих сборках хуки не ставятся.
 */
void addSpendHooks(Hooks& hooks, std::vector<std::string>& installed);

/** Одна запись таблицы хуков с адресом, подтверждённым только на Gog (схема 6.0 и дальше). */
struct GogHook
{
    const char* name;
    std::uintptr_t target;
    void* hook;
    void** original;
};

/**
 * Добавляет записи 'entries' на сборке Gog, если имя проходит probe/hooks.txt, и их имена - в
 * 'installed' (поле hooks строки start). На прочих сборках не ставит ничего. Defined in probe.cpp.
 */
void addGogHooks(const GogHook* entries,
                 std::size_t count,
                 Hooks& hooks,
                 std::vector<std::string>& installed);

template <std::size_t N>
void addGogHooks(const GogHook (&entries)[N], Hooks& hooks, std::vector<std::string>& installed)
{
    addGogHooks(entries, N, hooks, installed);
}

/**
 * Схема 6.0, партия 1: сохранение, захват деревни, рост земли, вход в сайт, снятие заклинаний,
 * создание предмета, очки хода и HP - probemap.cpp.
 */
void addMapHooks(Hooks& hooks, std::vector<std::string>& installed);

/** Схема 6.0, партия 1: проверки, опрашиваемые интерфейсом и ИИ, по ключу - probecheck.cpp. */
void addCheckHooks(Hooks& hooks, std::vector<std::string>& installed);

/** Схема 6.0, партия 1: действие, отмена и выбор вора, союз и обмен - probenoble.cpp. */
void addNobleHooks(Hooks& hooks, std::vector<std::string>& installed);

/**
 * Схема 6.0, партия 2: выбор навыка лидера, очередь выбора, зелье, расход боевых предметов, заряд
 * талисмана, опыт группы и Инструктор - probeleader.cpp.
 */
void addLeaderHooks(Hooks& hooks, std::vector<std::string>& installed);

/**
 * Схема 6.1: цель атак без урона - onHit двадцати классов IBatAttack, событие attack_hit, имена
 * hit_<класс> - probehit.cpp.
 */
void addHitHooks(Hooks& hooks, std::vector<std::string>& installed);

/**
 * Схема 6.2: запись состояния генератора боя (rng_seed, rng_reseed) и изменения запаса хода
 * отрядов (ap_restore) - probestate.cpp.
 */
void addStateHooks(Hooks& hooks, std::vector<std::string>& installed);

/**
 * Схема 6.3: перенос юнитов между группами (unit_swap, unit_dismiss -> unit_transfer), проверки
 * интерфейса swap_check, dismiss_check, equip_drop и тексты отказов ui_refusal - probeunit.cpp.
 */
void addUnitHooks(Hooks& hooks, std::vector<std::string>& installed);

/**
 * Схема 6.4: ход ИИ на карте - разрыв, предложение и подарок союза, ответы на союз и сделку,
 * стратегические действия и проходы, границы хода, приказы, бюджет, чат - probeai.cpp.
 */
void addAiHooks(Hooks& hooks, std::vector<std::string>& installed);

/**
 * Схема 6.5: экономический шаг ИИ - оценки, кандидаты, исполнение, бюджет, порядок городов, роль и
 * тип лидера, выбор юнита и здания, сырой CRT rand - probeaieco.cpp.
 */
void addAiEcoHooks(Hooks& hooks, std::vector<std::string>& installed);

/** Схема 6.5: тактика ИИ на карте - кандидаты, поправки, шаг, память, жезл, сила - probeaitac.cpp. */
void addAiTacticHooks(Hooks& hooks, std::vector<std::string>& installed);

/**
 * Схема 7.0/7.1: интерфейс и игровой цикл - создание и уничтожение диалогов (ui_open, ui_close), стек
 * экранов (ui_show, ui_hide), нажатия кнопок, переключателей и списков (ui_click), окна
 * сообщений (ui_msgbox), новая игра, загрузка и выход (game_phase) - probeui.cpp.
 */
void addUiHooks(Hooks& hooks, std::vector<std::string>& installed);

/** Схема 7.1: снимок экрана F12 с клеткой центра вида карты (screenshot) - probeshot.cpp. */
void addShotHooks(Hooks& hooks, std::vector<std::string>& installed);

/** Схема 7.1, часть 3: щелчок мышью по виду карты с клеткой и объектами в ней (map_click) - probeclick.cpp. */
void addClickHooks(Hooks& hooks, std::vector<std::string>& installed);

/**
 * Схема 7.2: данные ИИ для остатка расхождений порта - кэш мест и крепостей тактики с кошельком
 * (ai_place_cache, ai_fort_cache), сила группы по юнитам (ai_strength_units), дневное лечение
 * (regen), цель повышения (upgrade_target), порядок раскрытия узлов A* (path_expand), выдача id
 * (id_alloc) - probeai72.cpp.
 */
void addAi72Hooks(Hooks& hooks, std::vector<std::string>& installed);

/**
 * Схема 7.3: звук, музыка, ролики, анимации карты и боя, курсор, кадры - probeav.cpp и probeanim.cpp.
 * Miles и Bink - подменой ячеек импорта Discipl2.exe (видны только вызовы игры), движок - Detours.
 */
void addAvHooks(Hooks& hooks, std::vector<std::string>& installed);

/** Схема 7.3: анимации карты, кадры, курсор, прокрутка вида - probeanim.cpp (зовёт addAvHooks). */
void addAnimHooks(Hooks& hooks, std::vector<std::string>& installed);

/** Схема 7.3: показ боя - анимации юнитов, эффекты, касания, шаги, надписи - probebat.cpp (зовёт addAvHooks). */
void addBattleShowHooks(Hooks& hooks, std::vector<std::string>& installed);

/** Схема 7.6: портреты панели боя - вспышки, стойкие состояния, надписи с местом и концом - probeflash.cpp (зовёт addAvHooks). */
void addBattleFaceHooks(Hooks& hooks, std::vector<std::string>& installed);

/** Схема 7.6: метки карты - эллипс выбранного отряда и флажки пути - probemarks.cpp (зовёт addAvHooks). */
void addMapMarkHooks(Hooks& hooks, std::vector<std::string>& installed);

} // namespace hooks

#endif // PROBE_H
