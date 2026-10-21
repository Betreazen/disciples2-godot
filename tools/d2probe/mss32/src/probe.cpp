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
#include <atomic>
#include <chrono>
#include <cstddef>
#include <ctime>
#include <fmt/format.h>
#include <fstream>
#include <map>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <unordered_set>
#include <vector>
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

/* Git revision of the probe, passed as -p:ProbeGitHash=<hash> to MSBuild, "dev" otherwise. */
#define PROBE_STRINGIFY2(x) #x
#define PROBE_STRINGIFY(x) PROBE_STRINGIFY2(x)
#ifdef PROBE_GIT_HASH
#define PROBE_GIT_HASH_STRING PROBE_STRINGIFY(PROBE_GIT_HASH)
#else
#define PROBE_GIT_HASH_STRING "dev"
#endif

namespace probe {

namespace {

std::ofstream journal;
std::mutex journalMutex;
std::atomic<std::uint64_t> seqCounter{0};
std::atomic<std::uint64_t> rngCounter{0};
std::atomic<std::uint64_t> rngStdCounter{0};
std::atomic<bool> battleActiveFlag{false};
bool autoBattleMarker{false};

/* Window message pump calls, see probe::countPump(). Lock free: it is a render-loop hot path. */
std::atomic<std::uint64_t> pumpCounter{0};

/* Tick count at the moment init() opened the journal - the zero of the 't' field of every line. */
std::uint64_t journalOpenedTicks{0};
std::filesystem::path journalFile;

/* R250 state, Gog build only - see docs/re-rng.md (disciples-modding) and probe::rngIndexField(). */
constexpr std::uintptr_t rngIndexAddress = 0x838c24;

/*
 * Последние выданные генератором значения - кольцо на четыре записи под journalMutex.
 *
 * Зачем оно: строка `rng` пишется только пока идёт бой (см. setBattleActive), а бросок вора
 * всегда происходит на карте приключений, поэтому сами кубики в журнал не попадали ни разу -
 * разбор первой большой партии искал их у четырнадцати бросков и нашёл ноль из двадцати восьми.
 * Счётчик `rngCounter` при этом тикает всегда, значит достаточно запомнить рядом с номером
 * вызова его результат. Кольцо, а не «последние два»: читатель ищет по номеру вызова и потому
 * не может взять чужой, более поздний бросок.
 */
constexpr std::size_t rngRecentSize = 4;
std::uint64_t rngRecentN[rngRecentSize]{};
int rngRecentResult[rngRecentSize]{};

/**
 * Keyed logging state, see probe::emitKeyed(). Its own mutex, not journalMutex: emitKeyed() has to
 * decide whether to write at all before it touches the journal, and the counters are also read by
 * emitCounts() which then does write.
 *
 * Схема 7.4 (17.10.2026): ключи у каждого вида строки свои, со своим потолком, и снимаются при входе
 * в партию (probe::resetKeys(), хук game_enter). До 7.4 множество было одно на весь прогон с общим
 * потолком 200 000: в corpus/20261016e-av его к середине карты 5 заняли ключи move_cost (197 251 из
 * 200 357 - координаты, отряд, флаги), и на картах 6-7 не осталось ни одной строки *_check -
 * keys_dropped 1 112 966 к концу карты 5. Свой потолок не даёт частому виду вытеснить редкий, а сброс
 * на новой карте - карте вытеснить следующую.
 *
 * Схема 7.5 (18.10.2026): ai_rng пишется на все броски решений игры вне показа и боя (места -
 * probeairng.cpp), с цепочкой обёрток outer, именем origin и игроком хода ИИ player. Ключи хранятся
 * 64-битным хешем FNV-1a, а не строкой: узел - ~32 байта вместо ~120 (строка ключа move_cost ~70
 * символов), поэтому потолок move_cost поднят с 200 000 до 1 000 000 при той же памяти (карта 10
 * corpus/20261017-port-synth - 155 005 ключей за карту, 77 % прежнего потолка). Совпадение хешей двух
 * разных ключей при миллионе ключей - порядка 1e-7 за прогон; оно значило бы одну непоказанную строку.
 *
 * Схема 7.6 (20.10.2026): вспышки и стойкие состояния портретов боя, место и конец надписей
 * (probeflash.cpp), эллипс выбранного отряда и флажки пути на карте (probemarks.cpp), смена «кнопка
 * погашена» (ui_enable, probeui.cpp). Ключей у новых строк нет - пишутся только смены.
 */
struct KeyBucket
{
    std::unordered_set<std::uint64_t> seen; // хеши ключей вида с последнего входа в партию (7.5)
    std::uint64_t dropped{0};    // отброшено потолком вида за весь прогон
};

std::mutex keyedMutex;
std::map<std::string, KeyBucket, std::less<>> keyBuckets; // <ev> -> its keys
std::map<std::string, std::uint64_t> callCounts;          // <ev> -> calls
std::uint64_t keysDropped{0};                             // new keys refused, all kinds, whole run
std::uint64_t keysResets{0};                              // resetKeys() calls

/**
 * Ceilings on one kind's keys. The move_cost key carries x, y, six flags, the stack id and the
 * result, so on a big map it is unbounded in principle; without a cap the set would grow into
 * hundreds of thousands of heap strings over a long run. Keys past the cap are refused and counted
 * in keysDropped and the kind's own 'dropped', both printed by emitCounts() - the counters stay
 * truthful, only the sample of distinct combinations is cut off. move_cost had 200 000 (map 5 of
 * 20261016e-av alone gave 137 689) and since 7.5 has 1 000 000 hashed keys (same memory, see the
 * KeyBucket note); every other kind gets 20 000 - two orders above the busiest of them in that run
 * (equip_check, 806), and small enough that all of them together stay within a few times the old
 * single set.
 */
constexpr std::size_t moveCostKeyCap = 1000000;
constexpr std::size_t defaultKeyCap = 20000;

constexpr std::size_t keyCapOf(std::string_view event)
{
    return event == "move_cost" ? moveCostKeyCap : defaultKeyCap;
}

/** Схема 7.5: 64-битный FNV-1a ключа - то, что хранит множество вида. */
std::uint64_t keyHash(std::string_view key)
{
    std::uint64_t hash = 14695981039346656037ull;
    for (const unsigned char c : key) {
        hash ^= c;
        hash *= 1099511628211ull;
    }
    return hash;
}

/* Bucket of one kind, created on first use. Caller holds keyedMutex. */
KeyBucket& keyBucketOf(std::string_view event)
{
    auto it = keyBuckets.find(event);
    if (it == keyBuckets.end())
        it = keyBuckets.emplace(std::string{event}, KeyBucket{}).first;
    return it->second;
}

/**
 * Events whose calls are counted by countCall()/emitKeyed(). Seeded to zero by emitStart() so that
 * a hook which was installed but never fired shows up as an explicit "move_cost":0 instead of just
 * being absent from "counts" - the project rule is that an empty log must prove nothing by itself.
 * Whether a hook was installed at all is a separate question, answered by "hooks" in 'start'.
 */
constexpr std::string_view countedEvents[] = {
    // Schema v3 and v4.
    "move_cost", "can_move", "path", "stack_move", "xp", "build_status", "hire_list", "site",
    "income", "immune", "attack_new", "attack_get", "terrain_day",
    // Schema v5: treasury, map objects, items, city, thief, input - probeecon.cpp, probeinput.cpp.
    "bank", "site_buy_item", "site_sell_item", "site_buy_spell", "site_buy_unit", "site_train",
    "item_equip", "item_unequip", "heal", "revive", "city_grow", "build", "thief_roll",
    "thief_chance", "fort_day", "input",
    // Schema v5.2: diplomatic relations - probediplo.cpp.
    "relation",
    // Схема 5.3: причины трат казны - probespend.cpp.
    "hire_unit", "hire_leader", "rod_drop", "spell_cast", "spell_research", "wand_cast",
    // Схема 6.0, партия 1: сохранение, укрепления, земля, ход, проверки интерфейса, воры, союзы -
    // probemap.cpp, probecheck.cpp, probenoble.cpp.
    "save", "fort_capture", "land_grow", "land_reject", "site_visit", "spell_expire",
    "item_create", "ap_effect", "ap_apply", "hp_change", "garrison_max", "scout", "rod_check",
    "hire_check", "hire_leader_check", "merc_check", "sell_check", "grow_check", "noble_action",
    "thief_cancel", "noble_choose", "alliance_propose", "alliance_change", "trade_accept",
    // Схема 6.0, партия 2: лидеры, предметы, бой - probeleader.cpp, probecheck.cpp.
    "skill_offer", "skill_pick", "skill_pick_query", "upg_count", "equip_check", "wand_check",
    "potion_use", "item_consume", "talisman_charge", "xp_group", "weapon_master",
    // Схема 6.1: цель атак без урона - probehit.cpp.
    "attack_hit",
    // Схема 6.2: пересев генератора, запас хода - probestate.cpp.
    "rng_seed", "ap_restore",
    // Схема 6.3: перенос юнитов, отказы интерфейса - probeunit.cpp, probecheck.cpp.
    "unit_transfer", "swap_check", "dismiss_check", "equip_drop", "ui_refusal", "wand_precheck",
    // Схема 6.4: ход ИИ на карте - probeai.cpp.
    "ai_break_check", "ai_offer_check", "ai_gift_check", "ai_answer", "ai_strategic_step", "ai_pass",
    "ai_turn", "ai_order", "ai_budget", "ai_chat",
    // Схема 6.5: экономика и тактика ИИ - probeaieco.cpp, probeaitac.cpp.
    "ai_eval", "ai_candidate", "ai_exec", "ai_budget_spend", "ai_city_rank", "ai_leader_role",
    "ai_leader_pick", "ai_unit_pick", "ai_build_pick", "ai_rng", "crt_rand", "ai_tactic_candidate",
    "ai_tactic_adjust", "ai_tactic_pick", "ai_tactic_stack", "ai_tactic_result", "ai_rod_cell",
    "ai_strength",
    // Схема 7.0: тексты интерфейса по экранам - probeui.cpp (едет на хуке ui_refusal).
    "ui_text",
    // Схема 7.1: снимок экрана - probeshot.cpp; найм в отряд и сетка влияния ИИ - probeaitac.cpp.
    "screenshot", "ai_hire_score", "ai_grid", "ai_grid_fort",
    // Схема 7.1, часть 3: щелчок по карте - probeclick.cpp; генератор при открытии сценария -
    // probestate.cpp (едет на хуке game_start).
    "map_click", "rng_state",
    // Схема 7.2: данные ИИ для остатка расхождений порта - probeai72.cpp.
    "ai_place_cache", "ai_fort_cache", "ai_strength_units", "regen", "upgrade_target", "path_expand",
    "id_alloc",
    // Схема 7.3: звук, музыка, ролики - probeav.cpp; анимации карты и боя, курсор, кадры -
    // probeanim.cpp.
    "snd", "snd_end", "music", "video", "vol_set", "map_anim", "frames", "cursor", "view",
    "bat_anim", "bat_fx", "bat_effect", "bat_show", "bat_text",
    // Схема 7.6: портреты боя - вспышки и конец надписей (probeflash.cpp), метки карты (probemarks.cpp),
    // кнопки гаснут и зажигаются (probeui.cpp).
    "bat_flash", "bat_flash_end", "bat_text_end", "map_sel", "map_path", "ui_enable"};

/**
 * Parsed probe/hooks.txt whitelist: nullopt when the file is absent (every hook enabled), an
 * empty set when it exists but lists nothing (every hook disabled). Written once by init(), read
 * by hookEnabled() from getProbeHooks() afterwards - the engine runs single-threaded at that
 * point, no lock needed.
 */
std::optional<std::set<std::string>> hookWhitelist{};

/** Toolset bisection directives parsed from probe/hooks.txt, see hooks::ToolsetDirectives. Stays
 *  default (all false) when hooks.txt is absent or carries none of them. Written once by init(). */
hooks::ToolsetDirectives toolsetDirectivesState{};

/** Recognizes one 'toolset:...' line and folds it into 'state'. Returns false for anything else
 *  (including a malformed 'toolset:' line, left for the caller to treat as a plain hook name). */
bool parseToolsetDirective(const std::string& line, hooks::ToolsetDirectives& state)
{
    constexpr std::string_view prefix = "toolset:";
    if (line.compare(0, prefix.size(), prefix) != 0)
        return false;

    const std::string value = line.substr(prefix.size());
    if (value == "all") {
        state.rangeRequested = true;
        state.rangeAll = true;
        return true;
    }
    if (value == "vftable") {
        state.vftable = true;
        return true;
    }
    if (value == "restrictions") {
        state.restrictions = true;
        return true;
    }

    // 'A-B': two unsigned indices separated by '-'.
    const auto dash = value.find('-');
    if (dash == std::string::npos || dash == 0 || dash + 1 == value.size())
        return false;

    try {
        std::size_t afterFrom{};
        std::size_t afterTo{};
        const auto from = std::stoul(value.substr(0, dash), &afterFrom);
        const auto to = std::stoul(value.substr(dash + 1), &afterTo);
        if (afterFrom != dash || afterTo != value.size() - dash - 1 || from > to)
            return false;

        state.rangeRequested = true;
        state.rangeAll = false;
        state.rangeFrom = from;
        state.rangeTo = to;
        return true;
    } catch (const std::exception&) {
        return false;
    }
}

/** Parses probe/hooks.txt: one hook name (or 'toolset:' directive) per line, '#' starts a
 *  trailing comment, blanks skipped. */
std::set<std::string> parseHookWhitelist(const std::filesystem::path& path,
                                         hooks::ToolsetDirectives& toolset)
{
    std::set<std::string> names;
    std::ifstream file{path};
    std::string line;
    while (std::getline(file, line)) {
        const auto hash = line.find('#');
        if (hash != std::string::npos)
            line.erase(hash);

        line = hooks::trimSpaces(line);
        if (line.empty())
            continue;

        if (parseToolsetDirective(line, toolset))
            continue;

        names.insert(line);
    }

    return names;
}

std::string currentTimestamp()
{
    const auto now = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
    std::tm tm{};
    localtime_s(&tm, &now);

    return fmt::format("{:04d}{:02d}{:02d}-{:02d}{:02d}{:02d}", tm.tm_year + 1900, tm.tm_mon + 1,
                       tm.tm_mday, tm.tm_hour, tm.tm_min, tm.tm_sec);
}

const char* gameVersionName()
{
    switch (hooks::gameVersion()) {
    case hooks::GameVersion::Akella:
        return "Akella";
    case hooks::GameVersion::Russobit:
        return "Russobit";
    case hooks::GameVersion::Gog:
        return "Gog";
    case hooks::GameVersion::ScenarioEditor:
        return "ScenarioEditor";
    default:
        return "Unknown";
    }
}

std::uintmax_t exeSize()
{
    std::error_code ec;
    const auto size = std::filesystem::file_size(hooks::exePath(), ec);
    return ec ? 0u : size;
}

/** Caller must hold journalMutex. */
void writeLocked(std::uint64_t seq, std::string_view fields)
{
    // 't' (schema v5) is milliseconds since the journal was opened; the wall clock of that moment
    // is in the file name. Without it a player's click cannot be put next to a save file, and the
    // journal cannot say how long the engine spent between two events - 'seq' orders lines but
    // does not measure. Journals of schema v4 and older simply have no such field.
    const auto ms = GetTickCount64() - journalOpenedTicks;
    journal << fmt::format(R"({{"seq":{},"tid":{},"t":{},{}}})", seq, GetCurrentThreadId(), ms,
                           fields)
            << "\n";
    journal.flush();
}

} // namespace

void init(const std::filesystem::path& gameDir)
{
    std::error_code ec;
    const auto probeDir = gameDir / "probe";
    if (!std::filesystem::is_directory(probeDir, ec))
        return;

    journalOpenedTicks = GetTickCount64();
    const auto name = fmt::format("{:s}-{:d}.jsonl", currentTimestamp(), GetCurrentProcessId());
    const auto journalPath = probeDir / name;
    journalFile = journalPath;
    // Binary: the text mode of the Windows CRT turns every '\n' into "\r\n" and the trailing '\r'
    // ends up inside the parsed JSONL lines.
    journal.open(journalPath, std::ios::out | std::ios::trunc | std::ios::binary);
    if (!journal.is_open()) {
        // spdlog is unusable here: it pulls its bundled fmt, which clashes with <fmt/format.h>.
        hooks::showErrorMessageBox(
            fmt::format("Probe: could not open journal {:s}", journalPath.string()));
        return;
    }

    autoBattleMarker = std::filesystem::exists(probeDir / "autobattle", ec);

    const auto hooksFile = probeDir / "hooks.txt";
    if (std::filesystem::exists(hooksFile, ec))
        hookWhitelist = parseHookWhitelist(hooksFile, toolsetDirectivesState);

    // The 'start' line itself is written later, by emitStart(): only getProbeHooks() knows which
    // hooks survive the whitelist above and the null-target check, and nothing can reach the
    // journal before that hook table is committed, so 'start' still ends up first.
}

bool enabled()
{
    return journal.is_open();
}

bool autoBattleForced()
{
    return autoBattleMarker;
}

void setBattleActive(bool active)
{
    battleActiveFlag.store(active);
}

bool battleActive()
{
    return battleActiveFlag.load();
}

bool hookEnabled(const std::string& name)
{
    return !hookWhitelist || hookWhitelist->count(name) > 0;
}

bool hookExplicitlyEnabled(const std::string& name)
{
    return hookWhitelist && hookWhitelist->count(name) > 0;
}

const hooks::ToolsetDirectives& toolsetDirectives()
{
    return toolsetDirectivesState;
}

void emitStart(const std::vector<std::string>& hookNames,
              std::size_t toolsetCount,
              const std::vector<std::size_t>& toolsetSkipped)
{
    // Seed every counted event with a zero, so "counts" never omits one: without this a hook that
    // was installed but never fired is simply absent from "calls", and "the hook stood there and
    // was never called" reads exactly like "the hook never went in". Which hooks went in is the
    // separate question the "hooks" field below answers.
    {
        std::lock_guard<std::mutex> lock{keyedMutex};
        for (const auto event : countedEvents) {
            callCounts.emplace(std::string{event}, 0u);
        }
    }

    std::string hooksField{"["};
    const char* separator = "";
    for (const auto& name : hookNames) {
        hooksField += fmt::format(R"({}"{}")", separator, name);
        separator = ",";
    }
    hooksField += ']';

    // Only printed when at least one 'toolset:' directive was actually read: without hooks.txt
    // (or with one that carries none of them) the 'start' line stays exactly as before this
    // feature.
    std::string toolsetFields;
    const auto& t = toolsetDirectivesState;
    if (t.rangeRequested || t.vftable || t.restrictions) {
        std::string rangeField{"null"};
        if (t.rangeAll)
            rangeField = R"("all")";
        else if (t.rangeRequested)
            rangeField = fmt::format(R"("{}-{}")", t.rangeFrom, t.rangeTo);

        std::string skippedField{"["};
        const char* skipSeparator = "";
        for (const auto index : toolsetSkipped) {
            skippedField += fmt::format("{}{}", skipSeparator, index);
            skipSeparator = ",";
        }
        skippedField += ']';

        toolsetFields = fmt::format(
            R"(,"toolset_range":{},"toolset_count":{},"toolset_skipped":{},"toolset_vftable":{},"toolset_restrictions":{})",
            rangeField, toolsetCount, skippedField, t.vftable, t.restrictions);
    }

    emit(nextSeq(),
         fmt::format(
             R"("ev":"start","exe_size":{},"version":"{}","probe":"{}","schema":"7.6","autobattle":{},"hooks":{}{}{})",
             exeSize(), gameVersionName(), PROBE_GIT_HASH_STRING, autoBattleMarker, hooksField,
             toolsetFields, rngIndexField()));
}

std::uint64_t nextSeq()
{
    return ++seqCounter;
}

std::uint64_t currentSeq()
{
    return seqCounter.load(std::memory_order_relaxed);
}

std::uint64_t journalMs()
{
    return GetTickCount64() - journalOpenedTicks;
}

const std::filesystem::path& journalPath()
{
    return journalFile;
}

void emit(std::uint64_t seq, std::string_view fields)
{
    if (!journal.is_open())
        return;

    std::lock_guard<std::mutex> lock{journalMutex};
    writeLocked(seq, fields);
}

void countCall(std::string_view event)
{
    std::lock_guard<std::mutex> lock{keyedMutex};
    ++callCounts[std::string{event}];
}

void countPump() noexcept
{
    pumpCounter.fetch_add(1, std::memory_order_relaxed);
}

void emitKeyed(std::uint64_t seq,
               std::string_view event,
               std::string_view key,
               std::string_view fields)
{
    bool isNew = false;
    {
        std::lock_guard<std::mutex> lock{keyedMutex};
        ++callCounts[std::string{event}];

        auto& bucket = keyBucketOf(event);
        const auto inserted = bucket.seen.insert(keyHash(key));
        isNew = inserted.second;
        // Over the kind's ceiling: take the key back out and count the refusal instead of writing a
        // line. Erasing the iterator we just got costs nothing and keeps the common path one lookup.
        if (isNew && bucket.seen.size() > keyCapOf(event)) {
            bucket.seen.erase(inserted.first);
            ++bucket.dropped;
            ++keysDropped;
            isNew = false;
        }
    }

    if (isNew)
        emit(seq, fields);
}

void resetKeys()
{
    // The sets are swapped out under the lock and freed after it, when 'forgotten' goes out of
    // scope: a long map leaves hundreds of thousands of nodes, and freeing them under keyedMutex
    // would stall every emitKeyed() caller (pathfinding) for that long.
    std::vector<std::unordered_set<std::uint64_t>> forgotten;
    {
        std::lock_guard<std::mutex> lock{keyedMutex};
        forgotten.resize(keyBuckets.size());
        std::size_t i = 0;
        for (auto& entry : keyBuckets)
            forgotten[i++].swap(entry.second.seen);
        ++keysResets;
    }
}

void emitCounts()
{
    if (!journal.is_open())
        return;

    // try_to_lock on both locks, never a blocking wait: emitCounts() also runs from DllMain on
    // DLL_PROCESS_DETACH, and a normal exit is ExitProcess - Windows kills the other threads first
    // and only then calls DllMain under the loader lock. A thread killed inside emit() leaves the
    // journal mutex held forever, and a blocking lock here would hang the game on exit, right in
    // the acceptance scenario that asks the player to quit. A missed counts line is a lost
    // observation; a hung exit is a lost run.
    std::unique_lock<std::mutex> counters{keyedMutex, std::try_to_lock};
    if (!counters.owns_lock())
        return;

    std::string out{R"("ev":"counts","keys_dropped":)"};
    // Схема 7.4: отброшенные по видам (только виды с отказами; пусто - отказов не было) и число
    // сбросов ключей при входе в партию.
    out += fmt::format(R"({},"keys_dropped_by":{{)", keysDropped);
    const char* dropSeparator = "";
    for (const auto& entry : keyBuckets) {
        if (entry.second.dropped == 0)
            continue;
        out += fmt::format(R"({}"{}":{})", dropSeparator, entry.first, entry.second.dropped);
        dropSeparator = ",";
    }
    out += fmt::format(R"(}},"keys_resets":{},"input_pump":{},"calls":{{)", keysResets,
                       pumpCounter.load(std::memory_order_relaxed));
    const char* separator = "";
    for (const auto& entry : callCounts) {
        out += fmt::format(R"({}"{}":{})", separator, entry.first, entry.second);
        separator = ",";
    }
    out += "}";
    counters.unlock();
    // Схема 7.3: файл покрытия переписывается вместе со счётчиками; поле - только при покрытии.
    out += dumpCoverage();

    std::unique_lock<std::mutex> writing{journalMutex, std::try_to_lock};
    if (!writing.owns_lock())
        return;

    writeLocked(nextSeq(), out);
}

void emitRng(std::uint64_t seq, bool isStd, unsigned int maxValue, int result)
{
    if (!journal.is_open())
        return;

    // Locked before the increment, not after: README claims 'n' matches the order of the lines,
    // and that only holds while the same mutex covers both the counter and the write.
    std::lock_guard<std::mutex> lock{journalMutex};

    // The counter advances regardless of setBattleActive(): miss/dmg's rng_before/rng_after stay
    // meaningful even outside battle. Only the rng/rng_std line itself is scoped to battle time -
    // see setBattleActive().
    const auto n = isStd ? ++rngStdCounter : ++rngCounter;

    // Кольцо заполняется ДО проверки на бой: значения нужны как раз тем событиям, что живут вне
    // боя (thief_roll), а строка `rng` для них по-прежнему не пишется.
    if (!isStd) {
        const auto slot = static_cast<std::size_t>(n % rngRecentSize);
        rngRecentN[slot] = n;
        rngRecentResult[slot] = result;
    }

    if (!battleActiveFlag.load())
        return;

    // rng_index is the engine's R250 index, not the CRT rand() state rng_std reads from - it only
    // applies to the "rng" line.
    writeLocked(seq, fmt::format(R"("ev":"{}","n":{},"max":{},"r":{}{})",
                                 isStd ? "rng_std" : "rng", n, maxValue, result,
                                 isStd ? std::string{} : rngIndexField()));
}

std::string idToString(const game::CMidgardID* id)
{
    return id ? hooks::idToString(id) : std::string{"null"};
}

std::uint64_t rngCount()
{
    return rngCounter.load();
}

std::uint64_t rngStdCount()
{
    return rngStdCounter.load();
}

int rngResultAt(std::uint64_t n)
{
    std::lock_guard<std::mutex> lock{journalMutex};

    // n нумеруется с единицы, поэтому нулём кольцо и помечено как незаполненное.
    const auto slot = static_cast<std::size_t>(n % rngRecentSize);
    return n != 0 && rngRecentN[slot] == n ? rngRecentResult[slot] : -1;
}

std::string rngIndexField()
{
    if (hooks::gameVersion() != hooks::GameVersion::Gog)
        return {};

    const auto index = *reinterpret_cast<const volatile std::uint32_t*>(rngIndexAddress);
    return fmt::format(R"(,"rng_index":{})", index);
}

std::string hexAddress(std::uintptr_t address)
{
    return fmt::format("0x{:x}", address);
}

} // namespace probe

namespace hooks {

void addGogHooks(const GogHook* entries,
                 std::size_t count,
                 Hooks& hooks,
                 std::vector<std::string>& installed)
{
    // Адреса подтверждены только на Gog; на прочих сборках хуки не ставятся, как нуль в таблице.
    if (gameVersion() != GameVersion::Gog)
        return;

    for (std::size_t i = 0; i < count; ++i) {
        const auto& entry = entries[i];
        if (!probe::hookEnabled(entry.name))
            continue;

        hooks.push_back({reinterpret_cast<void*>(entry.target), entry.hook, entry.original});
        installed.push_back(entry.name);
    }
}

} // namespace hooks
