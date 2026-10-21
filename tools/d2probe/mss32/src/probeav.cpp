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
#include <cstdint>
#include <fmt/format.h>
#include <map>
#include <mutex>
#include <string>
#include <vector>
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

/*
 * Схема 7.3: звук, музыка, ролики - то, что слышно и видно, в журнал, чтобы харнесс сравнивал его с
 * журналом порта (docs/corpus-schema.md, «Звук, музыка, анимации»; src/D2.Data/Diagnostics/AvJournal.cs).
 * Строки дешёвые: одна на звук (старт и конец), одна на смену дорожки, громкость - сводкой.
 *
 * Два уровня.
 *
 * 1. Импорт Miles и Bink (ячейки IAT Discipl2.exe, Gog): зонд меняет указатель в ячейке, а не код
 *    mss23.dll - видны только вызовы самой игры, а не внутренние вызовы Bink (BinkOpenMiles) в тот же
 *    Miles. Ячейки: AIL_open_stream 0x6cc4b0, AIL_start_stream 0x6cc4a4, AIL_close_stream 0x6cc47c,
 *    AIL_pause_stream 0x6cc4a0, AIL_set_stream_volume 0x6cc4b4, AIL_set_stream_position 0x6cc494,
 *    AIL_start_sample 0x6cc4c4, AIL_end_sample 0x6cc4cc, AIL_set_sample_volume 0x6cc4d8,
 *    AIL_set_sample_pan 0x6cc4c8; BinkOpen 0x6cc44c, BinkClose 0x6cc440, BinkDoFrame 0x6cc42c. Все вызовы
 *    Miles игры - в CSoundSystemImpl (0x51fe00..0x521c00): open_stream(dig, путь, 0) - 0x52130b,
 *    start_sample - 0x520573 в playSample 0x52040d.
 *
 * 2. Движок - имя звука. Miles получает только байты (запись .wdb), имя знает «играть по имени»:
 *    0x56731a __thiscall(банк, const char* имя, {int vol 0..100; int pan 0..127}*, функтор*) ret 0xc,
 *    возвращает номер звука или 0 (не нашёлся). Зовут: 0x5b03e9 (общий проигрыватель карты и окон,
 *    адрес возврата 0x5b0433), 0x5c1dec (бой, 0x5c1f00) и 0x56bc29. Фон мест (петля) - 0x5674ce
 *    __thiscall(банк, имя, params*, a3, a4) ret 0x10, зовут 0x50926b/0x50999b. Хук запоминает имя в
 *    потоковой переменной, хук AIL_start_sample внутри того же вызова привязывает его к HSAMPLE, и строка
 *    `snd` пишется после оригинала - с номером, громкостью Miles (`out`, первый set_sample_volume) и
 *    признаком `ok`. Конец звука - колбэк конца сэмпла движка 0x5205d9 __stdcall(HSAMPLE) ret 4
 *    (`how":"end"`) или AIL_end_sample (`"stop"`): строка `snd_end` с длительностью и громкостью от и
 *    до - фон мест меняет громкость при прокрутке, это одна сводка, а не строка на смену.
 *
 * Музыка - поток Miles: `music` open (путь и имя дорожки), start, stop (close_stream), pause/resume,
 * seek (байты потока), volume - смены громкости одной дорожки, идущие подряд с шагом до 300 мс,
 * сливаются в одну строку «от, до, мс, шагов» (затухание CMusicFader - десятки вызовов). Строка
 * затухания пишется, когда оно кончилось: следующей сменой не подряд, открытием, снятием дорожки или
 * смене дня (emitCounts).
 *
 * Ролики Bink: `video` open (имя, кадров, кадров в секунду - из заголовка HBINK: Frames +8,
 * FrameRate +0x14, FrameRateDiv +0x18), close (показано кадров BinkDoFrame, мс).
 */

namespace probe {

namespace {

// ---------------------------------------------------------------- общее

std::mutex avMutex;

/** Имя дорожки по пути: без каталога и расширения, верхним регистром. */
std::string trackName(const std::string& path)
{
    auto slash = path.find_last_of("\\/");
    auto name = slash == std::string::npos ? path : path.substr(slash + 1);
    const auto dot = name.find_last_of('.');
    if (dot != std::string::npos)
        name.erase(dot);
    for (auto& c : name)
        c = static_cast<char>(toupper(static_cast<unsigned char>(c)));
    return name;
}

/** Ячейка импорта exe: старый указатель в 'original', в ячейке - 'hook'. */
bool patchIat(std::uintptr_t slot, void* hook, void** original)
{
    auto** cell = reinterpret_cast<void**>(slot);
    if (!readable(cell, sizeof(void*)) || !*cell)
        return false;

    DWORD old = 0;
    if (!VirtualProtect(cell, sizeof(void*), PAGE_READWRITE, &old))
        return false;

    *original = *cell;
    *cell = hook;
    VirtualProtect(cell, sizeof(void*), old, &old);
    return true;
}

// ---------------------------------------------------------------- звук

/** Звук, запрошенный движком по имени, пока идёт вызов «играть по имени» (тот же поток). */
struct PendingSound
{
    const char* name{};
    void* sample{};
    int output{-1};
};

thread_local PendingSound* pendingSound{nullptr};

/** Звучащий сэмпл Miles: имя, номер, начало, громкость. */
struct LiveSample
{
    std::string name;
    long long id{};
    std::uint64_t startMs{};
    int last{-1};
    int min{-1};
    int max{-1};
    int changes{};
};

std::map<void*, LiveSample> liveSamples;

void noteVolume(LiveSample& live, int output)
{
    if (output == live.last)
        return;
    if (live.last >= 0)
        ++live.changes;
    live.last = output;
    live.min = live.min < 0 ? output : (std::min)(live.min, output);
    live.max = (std::max)(live.max, output);
}

void endSample(void* sample, const char* how)
{
    LiveSample live;
    {
        std::lock_guard<std::mutex> lock{avMutex};
        const auto it = liveSamples.find(sample);
        if (it == liveSamples.end())
            return;
        live = it->second;
        liveSamples.erase(it);
    }
    countCall("snd_end");
    emit(nextSeq(), fmt::format(
                        R"("ev":"snd_end","id":{},"name":"{}","how":"{}","ms":{},"vol_min":{},"vol_max":{},"changes":{})",
                        live.id, live.name, how, journalMs() - live.startMs, live.min, live.max,
                        live.changes));
}

/** Архив и кто просил - по адресу возврата из «играть по имени» (re-sound §1.3, ранг 1). */
struct SoundSource
{
    const char* archive;
    const char* via;
};

SoundSource sourceOf(std::uintptr_t returnAddress)
{
    switch (returnAddress) {
    case 0x5b0433:
        return {"Midgard", "game"};
    case 0x5c1f00:
        return {"Battle", "battle"};
    case 0x56bc2e:
        return {"Capital", "capital"};
    case 0x509270:
    case 0x5099a0:
        return {"AudioRgn", "region"};
    default:
        return {nullptr, "other"};
    }
}

/** Адрес возврата из общего проигрывателя Sounds::playByName 0x5b03e9 - вид звука карты и окон. */
thread_local std::uintptr_t soundsCaller{0};

const char* whyOf(std::uintptr_t caller)
{
    switch (caller) {
    case 0x5b03e6:
        return "effect";
    case 0x5b04f4:
    case 0x5b05b1:
        return "ack";
    case 0x5b0653:
    case 0x5b06e8:
        return "idle";
    case 0x5b0774:
        return "walk";
    case 0x5b07e5:
        return "move";
    case 0x5b0864:
    case 0x5b08d5:
        return "attack";
    case 0x5b0954:
        return "kill";
    case 0x5b09d5:
        return "spell";
    default:
        return nullptr;
    }
}

struct SoundParams
{
    int volume;
    int pan;
};

/** Общая часть хуков «играть по имени»: имя, оригинал, строка snd. */
template <typename Call>
int playNamed(const char* name, const SoundParams* params, bool loop, std::uintptr_t returnAddress,
              Call&& original)
{
    const auto seq = nextSeq();
    // Имя и параметры - до оригинала: буфер вызывающего может не пережить вызов.
    const auto text = readable(name, 1) ? jsonCString(name, 64) : std::string{};
    int volume = -1;
    int pan = -1;
    if (readable(params, sizeof(SoundParams))) {
        volume = params->volume;
        pan = params->pan;
    }
    PendingSound pending{name};
    int id = 0;
    {
        ScopedValue<PendingSound*> scope{pendingSound, &pending};
        id = original();
    }

    if (pending.sample) {
        std::lock_guard<std::mutex> lock{avMutex};
        const auto it = liveSamples.find(pending.sample);
        if (it != liveSamples.end()) {
            it->second.name = text;
            it->second.id = id;
        }
    }
    const auto source = sourceOf(returnAddress);
    const auto* why = returnAddress == 0x5b0433 ? whyOf(soundsCaller) : nullptr;
    countCall("snd");
    emit(seq, fmt::format(
                  R"("ev":"snd","name":"{}","archive":{},"vol":{},"pan":{},"loop":{},"out":{},"id":{},"ok":{},"via":"{}","why":{},"caller":{},"ret_addr":"{}",{})",
                  text, source.archive ? fmt::format(R"("{}")", source.archive) : std::string{"null"},
                  volume, pan, loop, pending.output < 0 ? "null" : std::to_string(pending.output),
                  id, id != 0 && pending.sample != nullptr, source.via,
                  why ? fmt::format(R"("{}")", why) : std::string{"null"},
                  returnAddress == 0x5b0433 && soundsCaller
                      ? fmt::format(R"("{}")", hexAddress(soundsCaller))
                      : std::string{"null"},
                  hexAddress(returnAddress), screenFields()));
    return id;
}

using PlayByName = int(__fastcall*)(void* bank, int edx, const char* name, const SoundParams* params,
                                    void* functor);
PlayByName origPlayByName{};

int __fastcall playByNameProbe(void* bank, int edx, const char* name, const SoundParams* params,
                               void* functor)
{
    const auto ret = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    return playNamed(name, params, false, ret,
                     [&] { return origPlayByName(bank, edx, name, params, functor); });
}

using PlayLoop = int(__fastcall*)(void* bank, int edx, const char* name, const SoundParams* params,
                                  int a3, int a4);
PlayLoop origPlayLoop{};

int __fastcall playLoopProbe(void* bank, int edx, const char* name, const SoundParams* params, int a3,
                             int a4)
{
    const auto ret = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    return playNamed(name, params, true, ret,
                     [&] { return origPlayLoop(bank, edx, name, params, a3, a4); });
}

using SoundsPlay = int(__fastcall*)(void* sounds, int edx, const char* name, int volume, void* functor);
SoundsPlay origSoundsPlay{};

int __fastcall soundsPlayProbe(void* sounds, int edx, const char* name, int volume, void* functor)
{
    ScopedValue<std::uintptr_t> scope{soundsCaller, reinterpret_cast<std::uintptr_t>(_ReturnAddress())};
    return origSoundsPlay(sounds, edx, name, volume, functor);
}

using SampleEnd = void(__stdcall*)(void* sample);
SampleEnd origSampleEnd{};

void __stdcall sampleEndProbe(void* sample)
{
    endSample(sample, "end");
    origSampleEnd(sample);
}

using AilSample1 = void(__stdcall*)(void* sample);
using AilSample2 = void(__stdcall*)(void* sample, int value);
AilSample1 origStartSample{};
AilSample1 origEndSample{};
AilSample2 origSetSampleVolume{};
AilSample2 origSetSamplePan{};

void __stdcall startSampleProbe(void* sample)
{
    {
        std::lock_guard<std::mutex> lock{avMutex};
        auto& live = liveSamples[sample];
        live = LiveSample{};
        live.name = "?";
        live.startMs = journalMs();
        if (pendingSound) {
            pendingSound->sample = sample;
            if (pendingSound->output >= 0)
                noteVolume(live, pendingSound->output);
        }
    }
    origStartSample(sample);
}

void __stdcall endSampleProbe(void* sample)
{
    endSample(sample, "stop");
    origEndSample(sample);
}

void __stdcall setSampleVolumeProbe(void* sample, int volume)
{
    if (pendingSound && !pendingSound->sample) {
        pendingSound->output = volume;
    } else {
        std::lock_guard<std::mutex> lock{avMutex};
        const auto it = liveSamples.find(sample);
        if (it != liveSamples.end())
            noteVolume(it->second, volume);
    }
    origSetSampleVolume(sample, volume);
}

void __stdcall setSamplePanProbe(void* sample, int pan)
{
    if (!pendingSound) {
        std::lock_guard<std::mutex> lock{avMutex};
        const auto it = liveSamples.find(sample);
        if (it != liveSamples.end())
            ++it->second.changes;
    }
    origSetSamplePan(sample, pan);
}

// ---------------------------------------------------------------- музыка

/** Поток музыки: имя дорожки и незаписанная серия смен громкости. */
struct Stream
{
    std::string track;
    int volume{-1};
    int rampFrom{-1};
    std::uint64_t rampStart{};
    std::uint64_t rampLast{};
    int rampSteps{};
};

std::map<void*, Stream> streams;

/** Серия смен громкости, идущих подряд с шагом не больше этого, - одно затухание. */
constexpr std::uint64_t rampGapMs = 300;

/** Готовая строка затухания (под avMutex) или пусто. */
std::string takeRamp(Stream& stream)
{
    if (stream.rampSteps == 0)
        return {};

    auto line = fmt::format(
        R"("ev":"music","action":"volume","track":"{}","from":{},"to":{},"ms":{},"steps":{})",
        stream.track, stream.rampFrom, stream.volume, stream.rampLast - stream.rampStart,
        stream.rampSteps);
    stream.rampSteps = 0;
    return line;
}

void emitMusic(const std::string& line)
{
    if (line.empty())
        return;
    countCall("music");
    emit(nextSeq(), line);
}

std::string musicLine(const char* action, const std::string& track, const std::string& extra = {})
{
    return fmt::format(R"("ev":"music","action":"{}","track":"{}"{})", action, track, extra);
}

using OpenStream = void*(__stdcall*)(void* digital, const char* path, int memory);
thread_local bool inTrackStart{false};
std::string currentTrack; // под avMutex: дорожка, открытая последней
using AilStream1 = void(__stdcall*)(void* stream);
using AilStream2 = void(__stdcall*)(void* stream, int value);
OpenStream origOpenStream{};
AilStream1 origStartStream{};
AilStream1 origCloseStream{};
AilStream2 origPauseStream{};
AilStream2 origSetStreamVolume{};
AilStream2 origSetStreamPosition{};

/** Трек потока (пусто - поток не музыки или открыт до зонда). */
std::string streamTrack(void* stream)
{
    std::lock_guard<std::mutex> lock{avMutex};
    const auto it = streams.find(stream);
    return it == streams.end() ? std::string{} : it->second.track;
}

void* __stdcall openStreamProbe(void* digital, const char* path, int memory)
{
    void* stream = origOpenStream(digital, path, memory);
    const auto file = readable(path, 1) ? jsonCString(path, 260) : std::string{};
    const auto track = trackName(file);
    if (stream) {
        std::lock_guard<std::mutex> lock{avMutex};
        streams[stream] = Stream{track};
        currentTrack = track;
    }
    // Строку open пишет старт дорожки 0x67cbb1 (вид, место, громкость); сюда - только поток без него.
    if (!inTrackStart)
        emitMusic(fmt::format(R"("ev":"music","action":"open","track":"{}","file":"{}","ok":{},"kind":null,"from":0,"vol":null,{})",
                              track, file, stream != nullptr, screenFields()));
    return stream;
}

/*
 * Старт дорожки - единственная точка 0x67cbb1 __stdcall(CSoundSystemStream*, const char* путь, int громкость,
 * int с_мс, bool перемотать) ret 0x14 -> HSTREAM (re-sound §5.2). Вид по адресу возврата: 0x67cb82 - основная с
 * начала, 0x67ce6a - временная (бой, столица, музыка события), 0x67cedc - возврат основной с места.
 */
using TrackStart = void*(__stdcall*)(void* system, const char* path, int volume, int fromMs, int seek);
TrackStart origTrackStart{};

void* __stdcall trackStartProbe(void* system, const char* path, int volume, int fromMs, int seek)
{
    const auto ret = reinterpret_cast<std::uintptr_t>(_ReturnAddress());
    const char* kind = ret == 0x67cb82 ? "main" : ret == 0x67ce6a ? "temp" : ret == 0x67cedc ? "resume" : "other";
    const auto seq = nextSeq(); // до оригинала: строки start/seek изнутри идут после open
    void* stream = nullptr;
    {
        ScopedValue<bool> scope{inTrackStart, true};
        stream = origTrackStart(system, path, volume, fromMs, seek);
    }
    const auto file = readable(path, 1) ? jsonCString(path, 260) : std::string{};
    countCall("music");
    emit(seq, fmt::format(R"("ev":"music","action":"open","track":"{}","file":"{}","ok":{},"kind":"{}","from":{},"vol":{},{})",
                          trackName(file), file, stream != nullptr, kind, (seek & 0xff) ? fromMs : 0, volume,
                          screenFields()));
    return stream;
}

/** Дорожка, открытая последней (для строк затухания: у CMusicFader свой поток, имя - здесь). */
std::string lastTrack()
{
    std::lock_guard<std::mutex> lock{avMutex};
    return currentTrack;
}

/*
 * Затухания CMusicFader (re-sound §5.3): 0x5afb08 __thiscall(fader, int мс) ret 4 - спад и стоп музыки;
 * 0x5afbe4 __thiscall(fader, int мс, SmartPointer функтор по значению (8 байт), int* номер) ret 0x10 - спад и
 * действие (бой, столица, окно события, меню); 0x5afd8a __thiscall(fader, int мс, int громкость) ret 8 -
 * нарастание (возврат основной после временной). Строка music fade - длительность, заданная движком, без
 * дрожания шагов; сами шаги - строка volume (сводка AIL_set_stream_volume).
 */
using FadeOut = void(__fastcall*)(void* fader, int edx, int ms);
using FadeOutThen = void(__fastcall*)(void* fader, int edx, int ms, std::uint32_t f0, std::uint32_t f1, int* out);
using FadeIn = void(__fastcall*)(void* fader, int edx, int ms, int volume);
FadeOut origFadeOut{};
FadeOutThen origFadeOutThen{};
FadeIn origFadeIn{};

void emitFade(const char* dir, const char* then, int ms, std::uintptr_t ret, int volume = -1)
{
    emitMusic(fmt::format(R"("ev":"music","action":"fade","track":"{}","dir":"{}","then":"{}","ms":{},"to":{},"ret_addr":"{}")",
                          lastTrack(), dir, then, ms, volume < 0 ? std::string{"null"} : std::to_string(volume),
                          hexAddress(ret)));
}

void __fastcall fadeOutProbe(void* fader, int edx, int ms)
{
    emitFade("out", "stop", ms, reinterpret_cast<std::uintptr_t>(_ReturnAddress()));
    origFadeOut(fader, edx, ms);
}

void __fastcall fadeOutThenProbe(void* fader, int edx, int ms, std::uint32_t f0, std::uint32_t f1, int* out)
{
    emitFade("out", "call", ms, reinterpret_cast<std::uintptr_t>(_ReturnAddress()));
    origFadeOutThen(fader, edx, ms, f0, f1, out);
}

void __fastcall fadeInProbe(void* fader, int edx, int ms, int volume)
{
    emitFade("in", "none", ms, reinterpret_cast<std::uintptr_t>(_ReturnAddress()), volume);
    origFadeIn(fader, edx, ms, volume);
}

/** Конец дорожки сам (CMidMusic, 0x5aa018 основная / 0x5aa15b временная, __thiscall(HSTREAM) ret 4). */
using TrackEnd = void(__fastcall*)(void* music, int edx, void* stream);
TrackEnd origMainEnd{};
TrackEnd origTempEnd{};

void trackEnded(void* stream, const char* kind)
{
    const auto track = streamTrack(stream);
    emitMusic(fmt::format(R"("ev":"music","action":"end","track":"{}","kind":"{}")", track, kind));
}

void __fastcall mainEndProbe(void* music, int edx, void* stream)
{
    trackEnded(stream, "main");
    origMainEnd(music, edx, stream);
}

void __fastcall tempEndProbe(void* music, int edx, void* stream)
{
    trackEnded(stream, "temp");
    origTempEnd(music, edx, stream);
}

/*
 * Громкости из настроек (re-sound §6): 0x51fff7 звук (FxVolume, шкала Miles 0..127), 0x521268 музыка
 * (MusicVolume), 0x5b0472 голоса (Acknoledgements, 0..100) - все __thiscall(int) ret 4. Строка vol_set.
 */
using SetVolume = void(__fastcall*)(void* thisptr, int edx, int value);
SetVolume origFxVolume{};
SetVolume origMusicVolume{};
SetVolume origVoiceVolume{};

void emitVolumeSet(const char* kind, int value)
{
    countCall("vol_set");
    emit(nextSeq(), fmt::format(R"("ev":"vol_set","kind":"{}","value":{})", kind, value));
}

void __fastcall fxVolumeProbe(void* thisptr, int edx, int value)
{
    emitVolumeSet("fx", value);
    origFxVolume(thisptr, edx, value);
}

void __fastcall musicVolumeProbe(void* thisptr, int edx, int value)
{
    emitVolumeSet("music", value);
    origMusicVolume(thisptr, edx, value);
}

void __fastcall voiceVolumeProbe(void* thisptr, int edx, int value)
{
    emitVolumeSet("voice", value);
    origVoiceVolume(thisptr, edx, value);
}

void __stdcall startStreamProbe(void* stream)
{
    origStartStream(stream);
    const auto track = streamTrack(stream);
    if (!track.empty())
        emitMusic(musicLine("start", track));
}

void __stdcall closeStreamProbe(void* stream)
{
    std::string ramp;
    std::string track;
    {
        std::lock_guard<std::mutex> lock{avMutex};
        const auto it = streams.find(stream);
        if (it != streams.end()) {
            ramp = takeRamp(it->second);
            track = it->second.track;
            streams.erase(it);
        }
    }
    origCloseStream(stream);
    emitMusic(ramp);
    if (!track.empty())
        emitMusic(musicLine("stop", track));
}

void __stdcall pauseStreamProbe(void* stream, int onOff)
{
    origPauseStream(stream, onOff);
    const auto track = streamTrack(stream);
    if (!track.empty())
        emitMusic(musicLine(onOff ? "pause" : "resume", track));
}

void __stdcall setStreamVolumeProbe(void* stream, int volume)
{
    std::string ramp;
    {
        std::lock_guard<std::mutex> lock{avMutex};
        const auto it = streams.find(stream);
        if (it != streams.end() && volume != it->second.volume) {
            auto& s = it->second;
            const auto now = journalMs();
            if (s.rampSteps > 0 && now - s.rampLast > rampGapMs)
                ramp = takeRamp(s);
            if (s.rampSteps == 0) {
                s.rampFrom = s.volume < 0 ? volume : s.volume;
                s.rampStart = now;
            }
            ++s.rampSteps;
            s.rampLast = now;
            s.volume = volume;
        }
    }
    origSetStreamVolume(stream, volume);
    emitMusic(ramp);
}

void __stdcall setStreamPositionProbe(void* stream, int offset)
{
    origSetStreamPosition(stream, offset);
    const auto track = streamTrack(stream);
    if (!track.empty())
        emitMusic(musicLine("seek", track, fmt::format(R"(,"pos":{})", offset)));
}

// ---------------------------------------------------------------- ролики

struct Video
{
    std::string name;
    long long frames{};
    double rate{};
    long long shown{};
    std::uint64_t startMs{};
};

std::map<void*, Video> videos;

using BinkOpenFn = void*(__stdcall*)(const char* name, unsigned flags);
using BinkHandleFn = void(__stdcall*)(void* bink);
using BinkFrameFn = int(__stdcall*)(void* bink);
BinkOpenFn origBinkOpen{};
BinkHandleFn origBinkClose{};
BinkFrameFn origBinkDoFrame{};

void* __stdcall binkOpenProbe(const char* name, unsigned flags)
{
    void* bink = origBinkOpen(name, flags);
    Video video{readable(name, 1) ? trackName(jsonCString(name, 260)) : std::string{"?"}};
    std::uint32_t frames = 0;
    std::uint32_t rate = 0;
    std::uint32_t rateDiv = 0;
    if (bink && readAt(bink, 8, frames) && readAt(bink, 0x14, rate) && readAt(bink, 0x18, rateDiv)) {
        video.frames = frames;
        video.rate = rateDiv ? static_cast<double>(rate) / rateDiv : 0.0;
    }
    video.startMs = journalMs();
    if (bink) {
        std::lock_guard<std::mutex> lock{avMutex};
        videos[bink] = video;
    }
    countCall("video");
    emit(nextSeq(), fmt::format(
                        R"("ev":"video","action":"open","name":"{}","frames":{},"rate":{:.3f},"shown":0,"ms":0,"ok":{},"flags":{},{})",
                        video.name, video.frames, video.rate, bink != nullptr, flags, screenFields()));
    return bink;
}

int __stdcall binkDoFrameProbe(void* bink)
{
    {
        std::lock_guard<std::mutex> lock{avMutex};
        const auto it = videos.find(bink);
        if (it != videos.end())
            ++it->second.shown;
    }
    return origBinkDoFrame(bink);
}

void __stdcall binkCloseProbe(void* bink)
{
    Video video;
    bool known = false;
    {
        std::lock_guard<std::mutex> lock{avMutex};
        const auto it = videos.find(bink);
        if (it != videos.end()) {
            video = it->second;
            known = true;
            videos.erase(it);
        }
    }
    origBinkClose(bink);
    if (!known)
        return;
    countCall("video");
    emit(nextSeq(), fmt::format(
                        R"("ev":"video","action":"close","name":"{}","frames":{},"rate":{:.3f},"shown":{},"ms":{})",
                        video.name, video.frames, video.rate, video.shown,
                        journalMs() - video.startMs));
}

struct IatHook
{
    const char* name;
    std::uintptr_t slot;
    void* hook;
    void** original;
};

} // namespace

void flushMusicRamps()
{
    std::vector<std::string> lines;
    {
        std::unique_lock<std::mutex> lock{avMutex, std::try_to_lock};
        if (!lock.owns_lock())
            return;
        for (auto& [stream, s] : streams) {
            if (s.rampSteps > 0 && journalMs() - s.rampLast > rampGapMs)
                lines.push_back(takeRamp(s));
        }
    }
    for (const auto& line : lines)
        emitMusic(line);
}

} // namespace probe

namespace hooks {

void addAvHooks(Hooks& hooks, std::vector<std::string>& installed)
{
    using namespace probe;

    if (gameVersion() != GameVersion::Gog || !executableIsGame())
        return;

    // clang-format off
    const GogHook entries[] = {
        {"snd_play", 0x56731a, (void*)playByNameProbe, (void**)&origPlayByName},
        {"snd_loop", 0x5674ce, (void*)playLoopProbe, (void**)&origPlayLoop},
        {"snd_eos", 0x5205d9, (void*)sampleEndProbe, (void**)&origSampleEnd},
        {"snd_sounds", 0x5b03e9, (void*)soundsPlayProbe, (void**)&origSoundsPlay},
        {"music_start", 0x67cbb1, (void*)trackStartProbe, (void**)&origTrackStart},
        {"music_fade_out", 0x5afb08, (void*)fadeOutProbe, (void**)&origFadeOut},
        {"music_fade_then", 0x5afbe4, (void*)fadeOutThenProbe, (void**)&origFadeOutThen},
        {"music_fade_in", 0x5afd8a, (void*)fadeInProbe, (void**)&origFadeIn},
        {"music_end_main", 0x5aa018, (void*)mainEndProbe, (void**)&origMainEnd},
        {"music_end_temp", 0x5aa15b, (void*)tempEndProbe, (void**)&origTempEnd},
        {"vol_fx", 0x51fff7, (void*)fxVolumeProbe, (void**)&origFxVolume},
        {"vol_music", 0x521268, (void*)musicVolumeProbe, (void**)&origMusicVolume},
        {"vol_voice", 0x5b0472, (void*)voiceVolumeProbe, (void**)&origVoiceVolume},
    };
    const IatHook imports[] = {
        {"ail_start_sample", 0x6cc4c4, (void*)startSampleProbe, (void**)&origStartSample},
        {"ail_end_sample", 0x6cc4cc, (void*)endSampleProbe, (void**)&origEndSample},
        {"ail_sample_volume", 0x6cc4d8, (void*)setSampleVolumeProbe, (void**)&origSetSampleVolume},
        {"ail_sample_pan", 0x6cc4c8, (void*)setSamplePanProbe, (void**)&origSetSamplePan},
        {"ail_open_stream", 0x6cc4b0, (void*)openStreamProbe, (void**)&origOpenStream},
        {"ail_start_stream", 0x6cc4a4, (void*)startStreamProbe, (void**)&origStartStream},
        {"ail_close_stream", 0x6cc47c, (void*)closeStreamProbe, (void**)&origCloseStream},
        {"ail_pause_stream", 0x6cc4a0, (void*)pauseStreamProbe, (void**)&origPauseStream},
        {"ail_stream_volume", 0x6cc4b4, (void*)setStreamVolumeProbe, (void**)&origSetStreamVolume},
        {"ail_stream_position", 0x6cc494, (void*)setStreamPositionProbe, (void**)&origSetStreamPosition},
        {"bink_open", 0x6cc44c, (void*)binkOpenProbe, (void**)&origBinkOpen},
        {"bink_close", 0x6cc440, (void*)binkCloseProbe, (void**)&origBinkClose},
        {"bink_frame", 0x6cc42c, (void*)binkDoFrameProbe, (void**)&origBinkDoFrame},
    };
    // clang-format on

    addGogHooks(entries, hooks, installed);
    addAnimHooks(hooks, installed);
    addBattleShowHooks(hooks, installed);
    addBattleFaceHooks(hooks, installed);
    addMapMarkHooks(hooks, installed);
    for (const auto& entry : imports) {
        if (hookEnabled(entry.name) && patchIat(entry.slot, entry.hook, entry.original))
            installed.push_back(entry.name);
    }
}

} // namespace hooks
