# Звук и музыка `Discipl2.exe` (GOG 3.01) — точки для зонда

Разбор 16.10.2026 по листингу capstone (`probe/d2dis.py`, свой помощник `probe/snd/q.py`), начала функций сверены со списком
Ghidra `status/procs.txt`. Ранги: **1** — проверено по листингу, **2** — по косвенным признакам, **3** — догадка.
`[x]` — dword по адресу x. «this» — ecx (`__thiscall`), аргументы — по порядку на стеке.

## 0. Общая схема (ранг 1)

```
воспроизведение звука:
  карта/интерфейс  Sounds 0x5b03ce(idx) / голоса 0x5b047e… ──┐
                                                             ├─► 0x5b03e9 Sounds::playByName ─► 0x56731a CWavStore::play (Midgard.wdb)
  бой              0x5c1dec ─► 0x5c1e28 ───────────────────────────────────────────────────► 0x56731a                (Battle.wdb)
  столица          0x56bbc9 CCityLoadersWdb::play ─────────────────────────────────────────► 0x56731a                (Capital.wdb, в GOG пуст)
  фон мест         CAudioRegionMgr 0x509368 / 0x508f9b ────────────────────────────────────► 0x5674ce CWavStore::playLoop (AudioRgn.wdb)
  окно события     DLG_EVENT_POPUP 0x4bda67 (SOUND, mp3-файл) ─► 0x5203f0 напрямую (мимо CWavStore)
                       0x56731a ─► 0x5203f0 ─► 0x52040d CSoundSystemSample::play ─► AIL_*  (id = счётчик +0x148)
конец звука: Miles-поток 0x5205d9 (EOS) ─► сообщение ─► главный поток 0x52079f ─► функтор ─► 0x5676d1 CWavStore::onEnd(id)
музыка: CMidMusic (0x5a9b76 основная / 0x5a9c5e временная) ─► плеер дорожки (CMidMusicData+0x20c) 0x67cb4f / 0x67cc05
        ─► 0x67cbb1 open+start ─► CSoundSystemStream 0x5212ee (AIL_open_stream) ; затухания — CMusicFader 0x5af952
```

**Зонд сам — `mss32.dll`-прокси** (`tools/d2probe/mss32/module.def` пересылает `_AIL_*` в `mss23`). Перехват на уровне Miles
возможен, но тогда пропадают имя, архив и причина; ниже — точки движка, Miles только как запасной путь.

Синглтоны (ранг 1): `CSoundSystemSample` — SmartPtr `0x835c18`, указатель `[0x835c1c]` (им пользуется EOS 0x5205d9);
`Sounds` (Midgard) — SmartPtr `0x838648`, `[0x83864c]`; `CMidMusic` — SmartPtr `0x838500`, `[0x838504]` (getter `0x5a981b`);
`CMusicFader` — `[0x838644]` (getter `0x5af952`, stdcall без аргументов); `CSoundSystemStream` — getter `0x520e6e`
(статический, guard `0x835c28`); боевой банк — SmartPtr `0x838898`, `[0x83889c]`.

---

## 1. Архив по `this` банка (CWavStore)

### 1.1 Цепочка до пути `.wdb` — ранг 1

`this` у `0x56731a`/`0x5674ce`/`0x56765a` — `CWavStore*`. Путь лежит в объекте:

```
d  = [this]          CWavStoreData*  (0x50 байт; +0x04 CSoundSystemSample*, +0x14 Wdb*, +0x18 кэш имён, +0x34 map id→звучащий)
w  = [d+0x14]        Wdb*            (SmartPtr<Wdb> на +0x10, данные на +0x14; присваивается в ctor 0x566fdc: 0x567097 add ecx,0x10; call 0x56802c)
wd = [w]             WdbData* (0x18)
o  = [wd]            MQDB-обёртка (4 байта)
f  = [o]             MQDBFile* (0x88), String filePath на +0
path = [f+8]         char*  — полный путь «<папка Sounds>\midgard.wdb» (_makepath)
```

Доказательство (`Wdb` ctor `0x56870f`, MQDB ctor `0x545c24`, `String::assign` `0x403c8b`):
```
0x56874a: mov dword ptr [esi], edi        ; Wdb.data = new WdbData(0x18)
0x56876b: call 0x545c24                   ; new MQDB(path)
0x568780: mov dword ptr [ecx], eax        ; WdbData[0] = MQDB*
0x545c5d: mov dword ptr [esi], edi        ; MQDB[0] = new MQDBFile(0x88)
0x545c6b: call 0x403c8b                   ; MQDBFile.filePath(+0) = path
0x403cab: push dword ptr [esi + 8]        ; String: +0 length, +8 char*
```
Писать `archive` = имя файла без пути и расширения; кэшировать по `this` (банков всего 4, создаются раз).

### 1.2 Где создаются банки — ранг 1

| Банк | Файл (указатель на имя) | Создание Wdb / CWavStore | Где хранится CWavStore* |
|---|---|---|---|
| Midgard | `[0x7aeb3c]`→`"midgard.wdb"`, грузит `0x5b98f7`→`0x5b0140` | `0x5b0195` / `0x5b01f6` | `[[0x83864c]] + 0x14` (SoundsData+0x14) |
| Battle | `[0x7aeb40]`→`"battle.wdb"` (+`[0x7aeb44]` `battle.wdt`), `0x5b99d3`→`0x5c0be8` | `0x5c1bb3` / `0x5c1d8a` | банк боя, `[obj+4]` (см. 0x5c1ee0) |
| AudioRgn | `[0x7a4630]`→`"AudioRgn.wdb"`, `0x507695` | `0x507758` / `0x50778d` | CAudioRegionMgr data `+0x1c` |
| Capital | `[0x7b1fc8]`→`"Capital.wdb"`, `CCityLoadersWdb` (vtable `0x6e563c`) | `0x56ba8e` / `0x56bb7f` | `[CCityLoadersWdb+4]+8`; в GOG архив пуст |

### 1.3 Если без цепочки — адрес возврата (ранг 1)

| Вызов | Адрес возврата | Архив |
|---|---|---|
| `0x56731a` из `Sounds::playByName` 0x5b03e9 | `0x5b0433` | Midgard |
| `0x56731a` из боевого 0x5c1e28 | `0x5c1f00` | Battle |
| `0x56731a` из `CCityLoadersWdb::play` 0x56bbc9 | `0x56bc2e` | Capital |
| `0x5674ce` из `CAudioRegionMgr::tick` 0x508f9b | `0x509270` | AudioRgn |
| `0x5674ce` из `CAudioRegionMgr::update` 0x509368 | `0x5099a0` | AudioRgn |

Других вызывающих у `0x56731a`/`0x5674ce` нет (поиск всех `call`).

---

## 2. Воспроизведение: CWavStore и CSoundSystemSample

| Адрес | Соглашение, ret | Аргументы | Возврат | Ранг |
|---|---|---|---|---|
| `0x56731a` CWavStore::play | thiscall(CWavStore*), `ret 0xc` | `const char* name`, `Params* {int vol 0..100; int pan 0..127}`, `SmartPointer* functor` (конец) | eax = id звука, 0 — не играет (нет имени в архиве `0x567189`, нет свободного канала, звук выключен) | 1 |
| `0x5674ce` CWavStore::playLoop | thiscall(CWavStore*), `ret 0x10` | `name`, `Params*`, `int loopCount` (0 = бесконечно), `SmartPointer* functor` | id или 0 | 1 |
| `0x5203f0` Sample::play (обёртка) | thiscall(CSoundSystemSample*), `ret 0x14` | `data`, `Params*`, `functor*`, `bool named`, `int size` → `0x52040d(data, params, loop=1, functor, named, size)` | id | 1 |
| `0x52040d` Sample::playEx | thiscall, `ret 0x18` | `data`, `Params*`, `int loop`, `functor*`, `bool named(mp3-файл)`, `int size` | id (старый `[d+0x148]`, затем ++) | 1 |

Params по умолчанию — ctor `0x51fb30`: vol 100, pan 64; `0x51fb40 setVol` (0..100), `0x51fb58 setPan` (0..127).
`0x5b03e9`: громкость −1 → 100.

`0x56731a` всегда петля 1 и функтор конца `0x5676d1`, связанный с `this`:
```
0x56736b: mov dword ptr [ebp + 8], 0x5676d1    ; memfn CWavStore::onEnd
0x567379: call 0x568274                        ; функтор(this, 0x5676d1)
0x56737e: push ebx / push ebx / push eax / push [ebp+0xc] / push [eax+0x20]   ; (data, params, functor, 0, 0)
0x567393: call 0x5203f0                         ; → 0x52040d(..., loop=1, ...)
0x567465: ret 0xc
```
`0x5674ce`: `loopCount != 0` — функтор `0x5676d1`; `0` — пустой функтор (у `0x52040d` петля 0 с непустым функтором отвергается):
```
0x56751c: cmp dword ptr [ebp + 0x10], ebx      ; loopCount
0x567540: push dword ptr [ebp + 0x10]          ; loop
0x567552: call 0x52040d
0x567585: call 0x52040d                         ; ветка loop=0: push ebx(0) вместо loop, пустой функтор
```
Слоты: `CSoundSystemSampleData` — `+0x24` soundFx (bool), `+0x28` fxVolume (0..127, шкала Miles), `+0x2c` 10 слотов по 0x1c
(`+0` HSAMPLE, `+4` data, `+8` id, `+0xc` vol, `+0x10` pan, `+0x14` функтор), `+0x144` число слотов, `+0x148` следующий id,
`+0x14c/+0x150` очередь концов (вектор {int id; SmartPointer functor}, 12 байт), `+0x15c` критическая секция.
Громкость в Miles: `vol × [d+0x28] / 100` (0x5204f5…0x520520 `AIL_set_sample_volume`), панорама — как есть (`AIL_set_sample_pan` 0x520530).

**Что писать** (хук на `0x56731a` и `0x5674ce`, после оригинала): `snd_start {archive, name, vol, pan, loop, id, caller}`,
`caller` — адрес возврата **на уровень выше** (у Midgard всегда 0x5b0433 — нужен возврат из `0x5b03e9`, см. §6).
Частота — раз на событие (фон мест — раз на вход источника в число ближайших).

---

## 3. Остановка и конец звука

| Адрес | Соглашение, ret | Аргументы | Смысл | Ранг |
|---|---|---|---|---|
| `0x5200c2` Sample::stop | thiscall(CSoundSystemSample*), `ret 4` | `int id` | единственный `AIL_end_sample` (0x520135); обнуляет слот и **заменяет функтор пустым** — конца (0x5676d1) не будет; al=1 (id=0 → 0) | 1 |
| `0x56765a` CWavStore::stop | thiscall(CWavStore*), `ret 4` | `int id` | ищет id в map `+0x34`, зовёт 0x5200c2, удаляет из map; al | 1 |
| `0x5b045a` Sounds::stop | thiscall(Sounds*), `ret 4` | `int id` (≤0 — ничего) | → `0x56765a` банка Midgard; зовут 0x4c1b90, 0x4e66c1, 0x4f6f75 (шаг глушит прежний), 0x4f72b0 | 1 |
| `0x5202b5` Sample::stopAll | thiscall, `ret` | — | 0x5200c2 по всем слотам; зовёт только 0x51ff70 (выключение) | 1 |
| `0x5205d9` EOS (Miles) | **stdcall**, `ret 4` | `HSAMPLE` | **поток Miles**; находит слот по HSAMPLE в `[[0x835c1c]]+0x2c`, ставит {id, функтор} в очередь `+0x14c` и шлёт сообщение | 1 |
| `0x52079f` диспетчер концов | thiscall(CSoundSystemSample*), `ret 8` | (2 арг. события, не нужны) | главный поток; снимает первую запись очереди и зовёт `functor(id)` | 1 |
| `0x5676d1` CWavStore::onEnd | thiscall(CWavStore*), `ret 4` | `int id` | главный поток; естественный конец звука из CWavStore (не петля 0, не остановленный): удаляет из map и зовёт функтор вызывающего | 1 |

Остановленный звук конца не получает:
```
0x520135: call dword ptr [0x6cc4cc]        ; AIL_end_sample(h)
0x520199: call 0x534c37                    ; слот.functor = пустой
0x5201b4: mov dword ptr [eax + edi + 0x30], ebx   ; data = 0
0x5201ba: mov dword ptr [eax + edi + 0x34], ebx   ; id = 0
```
Диспетчер (id берётся до вызова):
```
0x5207f6: mov esi, dword ptr [eax + 0x14c] ; первая запись
0x520800: mov eax, dword ptr [esi]         ; id
0x520842: push dword ptr [ebp - 0x28]      ; id
0x520847: call dword ptr [eax]             ; functor(id)
```
Нет свободного канала: `0x52048b` → `0x520710(functor, 0)` — в очередь кладётся конец с id 0 (сразу «кончился»).

**Что писать**: `snd_stop {id, caller}` на входе `0x5200c2` (если id ещё у зонда в живых — обрыв; возвраты: `0x56769d` —
через CWavStore::stop, `0x509049` — фон места дотух до 0, `0x5202d8` — stopAll, `0x4bd916` — окно события);
`snd_end {id, dur_ms}` — на входе `0x52079f` (прочитать id `[[d+0x14c]]`, если очередь не пуста) — ловит концы всех
звуков, в т.ч. окна события; либо `0x5676d1` (только CWavStore, там `this` → архив). EOS `0x5205d9` не брать: чужой поток.

---

## 4. Фон мест (CAudioRegionMgr) — ранг 1, если не сказано иное

Классы (RTTI): `CAudioRegionMgr` vtable `0x6df75c` (`[0]` 0x508cd7 dtor, `[1]` 0x508f86 таймер), `CAudioRegionCtrl` vtable
`0x6df584` (`[0]` 0x507960 — вид сдвинулся), источники `CAudioRgnCapital/City/Landmark/Resource/Ruin/Site`.
Данные менеджера `d = [mgr+4]`: `+0x04` **CSoundSystemSample\*** (громкость/панорама по id идут мимо CWavStore),
`+0x10` включён (bool), `+0x14` N (сколько звучит), `+0x1c` CWavStore* (AudioRgn), `+0x34` звучащие (запись 0x18:
`+4` источник, `+8` id, `+0xc` цель vol, `+0x10` шаг, `+0x14` pan), `+0x48` дотухающие, `+0x54` кандидаты,
`+0x64` счётчик пауз, `+0x68/+0x70` последний вид, `+0x78` «идёт шаг затухания».

| Адрес | Соглашение, ret | Аргументы | Смысл, частота |
|---|---|---|---|
| `0x509368` update | thiscall(mgr), `ret 8` | `POINT* view`, `POINT* centre` (точки движка) | пересчёт при сдвиге вида (из 0x507960 и resume): выбирает N ближайших, новые запускает `0x5674ce` (возврат 0x5099a0) с громкостью первого шага, ушедшие — в список дотухания. Зовётся на каждый сдвиг вида (прокрутка — часто, ранг 2) |
| `0x508f9b` tick | thiscall(mgr), `ret` | — | шаг затухания: всем звучащим `setVolume`/`setPan`, дотухшим (vol≤0) — `0x5200c2`; ждущих запускает `0x5674ce` (возврат 0x509270). Зовётся из таймера `0x508f86` пока `+0x78`, и в конце update. Шаг — 1000 мс / 66 ≈ 15 шагов (`0x509a9f(Δ, 1000)`: `idiv 0x42`) — период таймера 66 мс (ранг 2) |
| `0x5202ea` Sample::setVolume | thiscall(CSoundSystemSample*), `ret 8` | `int id`, `int vol 0..100` | `AIL_set_sample_volume` 0x520348 (vol×fx/100); вызывают **только** 0x50908f, 0x509141 (tick); al |
| `0x520395` Sample::setPan | thiscall, `ret 8` | `int id`, `int pan 0..127` | `AIL_set_sample_pan` 0x5203e3; только 0x50909f, 0x50915c (tick), панораму ставит каждый тик и без изменения |
| `0x520357` Sample::getVolume | thiscall, `ret 4` | `int id` | 0..100 (0 — нет id) |
| `0x5208a8` Sample::freeChannels | thiscall, `ret` | — | число слотов со статусом SMP_DONE |
| `0x508e1a` pause | thiscall(mgr), `ret` | — | `++[d+0x64]`, глушит всё через `0x56765a` (0x508e79, 0x508ef0) |
| `0x508f42` resume | thiscall(mgr), `ret` | — | `--[d+0x64]`, update по сохранённому виду |
| `0x508d85` enable | thiscall(mgr), `ret 4` | `bool on` | `+0x10 = on`; on → resume, off → pause |

Пауза/возврат по окнам — RAII-страж `0x507b0f` (ctor → `0x4057bb` пауза, если `AudioRegions > 0`) / `0x507b23` (dtor →
`0x4057e2`); стражи ставят 0x482103, 0x482278, 0x492cee, 0x4965a7, 0x4a7f6f, 0x4aea98, 0x4b0bcf, 0x4d7d67 (ранг 2 —
что это за окна, не разбирал).

Формулы (подтверждают порт): цель `100 − d·100/769` (`0x5098ce: imul eax,0x64 / idiv 0x301`), панорама
`Δ·63/12 + 64` (`0x50991b: imul eax,0x3f / idiv 0xc / add 0x40`), первая громкость нового — шаг `0x509a9f(цель, 1000)`.
```
0x509922: push 0x3e8 / push [ebp+8] / call 0x509a9f   ; шаг = цель / (1000/66)
0x509988: push 0                                      ; loopCount = 0 (бесконечно)
0x509991: call dword ptr [eax + 0x14]                 ; имя у источника (vcall)
0x50999b: call 0x5674ce
```
**Что писать**: `region_start {id, name, vol0, target, pan}` на выходе `0x5674ce` (archive=AudioRgn);
`region_stop {id, why: fade|pause}` — `0x5200c2` с возвратом 0x509049 / `0x56765a` с возвратом 0x508e7e, 0x508ef5;
`region_pause/resume {count=[d+0x64]}` — 0x508e1a / 0x508f42; `region_view {x,y}` — на входе 0x509368 **с прореживанием**
(не чаще раза в 250 мс или только когда сменился набор id). `setVolume/setPan` не писать каждый тик: держать последнее
значение по id и писать `region_mix {id, vol, pan}` только при достижении цели (шаг `+0x10` обнулён) или раз в секунду.

---

## 5. Музыка

### 5.1 CMidMusic — ранг 1

Данные `m = [CMidMusic]` (632 байта): `+0x000` папка музыки `char[260]`, `+0x104` своя папка (mp3), `+0x20c` плеер дорожки
(объект `p`, §5.2), `+0x214` CSoundSystemStream*, `+0x218` вид основной, `+0x21c` вид временной, `+0x220` LRaceCategory
столицы, `+0x22c/+0x23c` String папка/имя основной вида 2, `+0x24c/+0x25c` — временной вида 2, `+0x26c` функтор конца
(вид 3), `+0x274` «по концу продолжать» (bool).

| Адрес | Соглашение, ret | Аргументы | Смысл |
|---|---|---|---|
| `0x5a9b76` playMain | thiscall(CMidMusic*), `ret 0x14` | `const char* folder`, `const char* name`, `int kind`, `int mp3`, `bool repeat` | путь `_makepath(buf, 0, folder, name, mp3 ? ".MP3" : ".WAV")` → `p.playMain(path)` 0x67cb4f; конец — `0x5aa018`. Не играет, если нет потока, музыка выключена (`0x67e40c`) или громкость 0 (`0x580cc6`) |
| `0x5a9c5e` playTemp | thiscall, `ret 0x10` | `folder`, `name`, `int kind`, `int mp3` | путь так же → `p.playTemp(path, 0)` 0x67cc05; конец — `0x5aa15b`. (D2ModdingToolset: PlayFile) |
| `0x5a9d38` playMenu | thiscall, `ret` | — | playMain(m+0, `MENUTRCK` `[0x7ae43c]`, kind 0, wav, repeat 1) |
| `0x5a9d57` playMap | thiscall, `ret` | — | имя — `0x5b92a8` (imusic.dbf); playMain(m+0 или m+0x104, name, kind 1, mp3 = своя папка, 1) |
| `0x5a9eeb` playMainFile | thiscall, `ret 0xc` | `folder`, `name`, `bool repeat` | kind 2, wav (бриф: `GENBRIEF`/по расе, 0x48091e) |
| `0x5a9f44` playTune | thiscall, `ret 0x10` | `folder`, `name`, `SmartPointer functor` (по значению) | kind 3, функтор по концу (эпилог 0x4bca73, 0x4bcdcc) |
| `0x5a9ff9` playCredits | thiscall, `ret` | — | kind 5, `CREDITS` `[0x7ae45c]` |
| `0x5a9dd7` playBattle | thiscall, `ret` | — | счётчик `[0x7ae178]` (−1 → rand(4)), 1..4 по кругу; `"BATTLE0"`+n; playTemp(m+0, name, kind 0, wav) |
| `0x5aa1b9` playCapital | thiscall, `ret 4` | `LRaceCategory*` | тема расы (`HUMNTRK`/`UNDETRK`/`DWRFTRK`/`HERETRK`/`ELFTRK`, `[0x7ae448…0x7ae458]`) + счётчик `[0x838508]` 1..3; playTemp(…, kind 1, wav) |
| `0x5a9e95` playEventMusic | thiscall, `ret 8` | `folder`, `name` | playTemp(kind 2, mp3) — MUSIC окна события (0x4bd9be) |
| `0x5aa30e` endTemp | thiscall, `ret` | — | снимает функтор конца временной, `p.resumeMain(0)` 0x67ce80, затем `fader.fadeIn(1000, musicVol)` |
| `0x5a9b69` stop | thiscall, `ret` | — | `p.stop()` 0x67ceeb |
| `0x5aa018` onMainEnd | thiscall, `ret 4` | `HSTREAM` | конец основной: при `+0x274` по `+0x218`: 0 меню снова, 1 следующая карты, 2 тот же файл, 3/4 функтор `+0x270`, 5 титры |
| `0x5aa15b` onTempEnd | thiscall, `ret 4` | `HSTREAM` | конец временной по `+0x21c`: 0 следующий бой, 1 следующая тема столицы, 2 музыка события снова |

```
0x5a9bc8: mov eax, 0x6eb688 ; ".WAV"   (0x6eb680 ".MP3" при mp3)
0x5a9be3: call dword ptr [0x6cc2ac]       ; _makepath(buf, NULL, folder, name, ext)
0x5a9bf1: mov dword ptr [eax + 0x218], ecx; kind
0x5a9c06: call 0x67cb4f                   ; p.playMain(path)
0x5a9c12: mov dword ptr [ebp + 0x14], 0x5aa018 ; конец основной
0x5a9c48: mov byte ptr [eax + 0x274], cl  ; repeat
```

### 5.2 Плеер дорожки (CMidMusicData+0x20c) и поток — ранг 1

Один поток на всё: `q = [p]`: `+0x04` CSoundSystemStream*, `+0x44/+0x48` конец/длина своего затухания, `+0x4c` char* путь
временной, `+0x50` char* путь основной, `+0x54` место основной (мс), `+0x58` HSTREAM, `+0x5c` «сейчас основная».

| Адрес | Соглашение, ret | Аргументы | Смысл |
|---|---|---|---|
| `0x67cb4f` playMain | thiscall(p), `ret 4` | `const char* path` | закрыть текущий, открыть с начала, `+0x50=path`, `+0x54=0`, `+0x5c=1`; al |
| `0x67cc05` playTemp | thiscall(p), `ret 8` | `path`, `int fadeMs` | `+0x4c=path`; движок всегда даёт fadeMs=0 → сразу сообщение → `0x67ce0a`: **место основной `AIL_stream_position` → `+0x54`**, закрыть, открыть временную с 0 |
| `0x67ce80` resumeMain | thiscall(p), `ret 4` | `int vol` (−1 → громкость музыки) | открыть `+0x50` с места `+0x54` (seek), `+0x5c=1` |
| `0x67ceeb` stop | thiscall(p), `ret` | — | закрыть текущий |
| **`0x67cbb1` openStart** | **stdcall**, `ret 0x14` | `CSoundSystemStream*`, `const char* path`, `int vol`, `int startMs`, `bool seek` | **единственное место старта дорожки**: `0x5212ee` open → seek (если seek && startMs>0) → петля 1 → громкость → start; eax=HSTREAM (0 — файла нет) |
| `0x5212ee` Stream::open | thiscall(CSoundSystemStream*), `ret 4` | `path` | `AIL_open_stream(dig, path, 0)` 0x52130b, громкость `[d+0x3c]`; HSTREAM |
| `0x521342` Stream::close | thiscall, `ret 4` | `HSTREAM` | пауза, из списка, `AIL_close_stream` 0x5213cd. Зовут 0x67cb11 и выключение 0x5210b1 |
| `0x52150e` Stream::setVolume | thiscall, `ret 8` | `HSTREAM`, `int vol 0..127` | `AIL_set_stream_volume` 0x52158b; зовёт **тик затухания каждый проход цикла** (0x5afe9c, 0x5aff1d) |
| `0x521268` Stream::setMusicVolume | thiscall, `ret 4` | `int vol` | `[d+0x3c]=vol`, переставляет громкость звучащих; из окна настроек 0x4c1c6d, начала 0x61b762, эпилога 0x4bc966/0x4bca73 |
| `0x5215a5`/`0x5215b6` | thiscall, `ret 8` / `ret 4` | `(h, ms)` / `(h)` | set/get место, мс |
| `0x52116f` конец потока | thiscall(Stream), `ret 8` | `(?, HSTREAM)` | главный поток (из EOS Miles `0x521109`): функтор временной `[d+0x30]`, иначе основной `[d+0x28]` |

`CSoundSystemStreamData`: `+0x28` функтор конца основной, `+0x30` временной, `+0x34` musicOn, `+0x38` счётчик пауз,
`+0x3c` громкость музыки (0..127), `+0x40` список {HSTREAM, vol}.

```
0x67cbbe: call 0x5212ee            ; open(path)
0x67cbdb: call 0x5215a5            ; seek(h, startMs)   — только если seek && startMs>0
0x67cbe5: call 0x521448            ; loop_count(h, 1)
0x67cbf0: call 0x52150e            ; volume(h, vol)
0x67cbf8: call 0x5213da            ; start + register_stream_callback(0x521109)
0x67cc02: ret 0x14
```
Вызывающие `0x67cbb1` по адресу возврата: `0x67cb82` — основная с начала, `0x67ce6a` — временная, `0x67cedc` — возврат
основной с места.

### 5.3 CMusicFader — ранг 1

Данные `f = [fader+4]`: `+0x18` событие (есть — `0x5aff27`), `+0x20` HSTREAM, `+0x24` режим (1 — нарастание, 2 — затухание
и стоп музыки, 3 — затухание и функтор), `+0x28` направление (0 — вниз, 1 — вверх), `+0x2c/+0x30` функтор, `+0x34` номер
затухания (счётчик `[0x7ae384]`), `+0x38` громкость (исходная при спаде / цель при нарастании), `+0x3c` начало
(`timeGetTime`), `+0x40` длительность мс, `+0x48` CMidMusic*.

| Адрес | Соглашение, ret | Аргументы | Смысл |
|---|---|---|---|
| `0x5afb08` fadeOutStop | thiscall(fader), `ret 4` | `int ms` | режим 2; по концу `CMidMusic::stop`. Нет потока — ничего. (меню 0x480194, 0x4dd798, вступление 0x4eb193) |
| `0x5afbe4` fadeOutThen | thiscall, `ret 0x10` | `int ms`, `SmartPointer functor` (8 байт по значению), `int* outId` | режим 3; нет потока — функтор сразу. Бой 0x62daf8 (1000, → playBattle `0x5aff8c`), конец боя 0x62e4a6 (700, → endTemp `0x5aff5c`), столица 0x4af129 (1500), из столицы 0x4aef8c (700), окно события 0x4bd652/0x4bd972, меню 0x4e9fc5 и др. |
| `0x5afd8a` fadeIn | thiscall, `ret 8` | `int ms`, `int targetVol` | режим 1 (только из endTemp: 1000 мс) |
| `0x5aff35` reverse | thiscall, `ret` | — | разворот затухания на полпути (с пересчётом начала) |
| `0x5afd1e` cancel | thiscall, `ret 4` | `int id` | id совпал с текущим — функтор снят |
| `0x5afe61` tick | thiscall, `ret` (al=1) | — | событие **Update** (тип 2, `0x5614aa`) — **каждый проход цикла сообщений**; по истечении `ms` ставит итог и выполняет режим |

```
0x5afc69: call 0x521598            ; исходная громкость = AIL_stream_volume(h) → f+0x38
0x5afc7d: call dword ptr [0x6cc41c]; timeGetTime → f+0x3c ; ms → f+0x40
0x5afe76: cmp eax, edi / setg dl   ; tick: прошло > ms → конец
0x5afef5: imul eax,0x64 / idiv edi / 100−… ; спад: vol = v0·(100 − t·100/ms)/100
```
**Что писать** (музыка — раз на событие, кроме тика):
- `music_start {track=basename(path), kind: main|temp|resume (по возврату 0x67cbb1), from_ms = seek ? startMs : 0, vol, ok=h≠0}` — выход `0x67cbb1`;
  вид CMidMusic (`menu/map/brief/tune/credits` / `battle/capital/event`) — по `[m+0x218]`/`[m+0x21c]` или по входу в 0x5a9b76/0x5a9c5e (`kind`).
- `music_fade {dir: out|in, from=[f+0x38] (спад) | 0 (нарастание), to=0 | target, ms, then: stop|call|none}` — выход 0x5afb08/0x5afbe4/0x5afd8a
  (у 0x5afbe4 без потока — `skipped`); `music_fade_end` — в tick, когда `timeGetTime − [f+0x3c] > [f+0x40]` (до оригинала).
- `music_stop {track, pos_ms}` — вход `0x521342` (место — `0x5215b6(h)` до оригинала).
- `music_end {kind: main|temp}` — вход `0x5aa018` / `0x5aa15b` (естественный конец дорожки).
- `music_volume {vol}` — вход `0x521268`.
Тик `0x5afe61` и `0x52150e` из него не писать.

---

## 6. Громкости из настроек — ранг 1

`GameSettings` (`CMidgardData+60` → `GameSettings**`, раскладка `include/gamesettings.h` в dg-wt-probe сходится с кодом):

| Поле | Смещение | Сеттер (пишет и в ini) | Где применяется |
|---|---|---|---|
| soundFx (bool) | `+0x158` | — | `0x52006b` Sample::setSoundFx(bool) → `AIL_set_digital_master_volume`, `[d+0x24]` |
| musicOn (bool) | `+0x159` | — | `CSoundSystemStreamData+0x34` (`0x67e40c` читает) |
| FxVolume | `+0x15c` (0x61a417) | `0x61a417` | `0x51fff7` Sample::setFxVolume(int) thiscall `ret 4`: `[d+0x28]=v`, пересчёт всех звучащих `vol×v/100`; из окна 0x4c1b20 и начала 0x61b806. Значение — шкала Miles 0..127 |
| MusicVolume | `+0x160` (0x61a495) | `0x61a48c` | `0x521268` setMusicVolume; 0 → `CMidMusic::stop`, с 0 → playMap/playMenu (0x4c1d40…0x4c1d96) |
| Acknoledgements | `+0x164` (0x61a466) | `0x61a460` | `0x5b0472` Sounds::setVoiceVolume(int) thiscall `ret 4`: `SoundsData+0x28` (начально 100, 0x5b02a9); из окна 0x4c1bcb и начала 0x61b667 |
| AudioRegions | `+0x184` | — | getter `0x61a866`; >0 — паузы фона по окнам (0x4057bb) |
| pauseAudioRegionsForAi | `+0x188` | — | (не разбирал) |

```
0x51fff7: … mov dword ptr [eax + 0x28], ecx   ; fxVolume
0x520022: call 0x566368 / imul eax, [esi+0x28] / idiv 100
0x52004b: call dword ptr [0x6cc4d8]           ; AIL_set_sample_volume
0x61a420: mov dword ptr [eax + 0x15c], ecx    ; settings.FxVolume
0x61a466: mov dword ptr [eax + 0x164], edx    ; settings.Acknoledgements
0x5b0478: mov dword ptr [eax + 0x28], ecx     ; Sounds.voiceVolume
```
После смены FxVolume окно играет `SOUNDFX` (`0x5b03ce(0x20, −1)`, 0x4c1b60), после смены голосов — `ACK` (0x4c1c41).
**Что писать**: `vol_set {fx|music|voice, value}` на входе 0x51fff7 / 0x521268 / 0x5b0472 (раз на действие).

---

## 7. Голоса юнитов и звуки интерфейса — ранг 1, если не сказано иное

Все идут через `Sounds::playByName 0x5b03e9` (thiscall(Sounds*), `ret 0xc`: `const char* name`, `int vol` (−1 → 100),
`SmartPointer* functor`; eax — id или 0) → `0x56731a` банка **Midgard**. Перед игрой `0x5b09f4(name)` проверяет: есть
система, soundFx, FxVolume≠0. Голоса — громкостью `SoundsData+0x28` (Acknoledgements), не 100:

| Функция Sounds | ret | Имя | Адрес возврата из 0x5b03e9 |
|---|---|---|---|
| `0x5b03ce` playEffect(idx, vol, functor) | `ret 0xc` | таблица `[0x7ae468 + idx·4]` (46 служебных: APPEAR…CREATSTK, см. `wavstore.h`) | `0x5b03e6` |
| `0x5b047e` playAck(unitId*) | `ret 4` | `{тип}ACK{1,2,3}` (0x5b7c8e) | `0x5b04f4` |
| `0x5b0549` | `ret 4` | `ACKN` (0x5bfdbd) | `0x5b05b1` |
| `0x5b05dd` playIdle(unitId*) | `ret 4` | `{тип}IDLE{1,2}` (0x5b7e45) | `0x5b0653` |
| `0x5b0680` | `ret 4` | `IDLE` (0x5bff27) | `0x5b06e8` |
| `0x5b0714` (шаг, ранг 2: зовёт 0x4f6e78) | `ret 8` | имя из 0x5b7ded (строк нет — вероятно `walk` SNDINFO) | `0x5b0774` |
| `0x5b0793` | `ret 8` | `{тип}MOVE` (0x5bfe72) | `0x5b07e5` |
| `0x5b0804` playAttk(unitId*, functor) | `ret 8` | `{тип}ATTK` (0x5b7d57) | `0x5b0864` |
| `0x5b0883` | `ret 8` | `ATTK` по типу (0x5bffdc) | `0x5b08d5` |
| `0x5b08f4` playKill | `ret 8` | `{тип}KILL` (0x5b7f02) | `0x5b0954` |
| `0x5b0973` playBoat(functor) | `ret 4` | `0x5b03ce(1 = BOATSND, voiceVol)` | (через 0x5b03e6) |
| `0x5b0986` (ранг 3: звук по id из справочника — литьё?) | `ret 8` | 0x5b6311(id), громкость 100 | `0x5b09d5` |

```
0x5b04e7: lea ecx,[ebp-0x14] / push ecx      ; functor
0x5b04e9: push dword ptr [ecx + 0x28]         ; voiceVolume (SoundsData+0x28)
0x5b04ee: push eax                            ; имя
0x5b04ef: call 0x5b03e9
0x5b0426: mov eax,[esi] / mov ecx,[eax+0x14]  ; CWavStore Midgard
0x5b042e: call 0x56731a
```
Бой — свой путь: `0x5c1dec` (thiscall(банк боя), `ret 0xc`: `name`, `Params*`, `functor*`; al) → `0x5c1e28` → `0x56731a`
(Battle.wdb); проверяет систему, soundFx и FxVolume (`0x580cc0`). 14 вызывающих (0x635cb9…0x6533fd).
Озвучка окна события (`DLG_EVENT_POPUP`, SOUND из папки SoundsP, `.mp3`) — `0x4bda67` зовёт `0x5203f0` **напрямую** (named=1,
размер), мимо CWavStore и 0x56731a; стоп — `0x4bd911` → 0x5200c2. MUSIC окна — `0x5a9e95` (ранг 2 по строкам и вызовам).

**Что писать**: хук `0x5b03e9` (вход: name, vol; выход: id) с причиной по адресу возврата из таблицы и ещё на уровень выше
(вызывающий 0x5b03ce/0x5b047e…) — тогда хук 0x56731a для Midgard дублирует, оставить его только как `archive`-источник для
Battle/Capital (или писать 0x56731a всегда, а 0x5b03e9 — только `why`).

---

## 8. Минимальный набор хуков (рекомендация)

1. `0x56731a`, `0x5674ce` — старт (архив по цепочке §1.1), выход: id.
2. `0x5b03e9` — причина звуков карты/интерфейса (возврат), голоса.
3. `0x5200c2` — обрыв; `0x52079f` — естественный конец (id из очереди до вызова).
4. `0x67cbb1` — старт дорожки (путь, место, вид по возврату); `0x521342` — стоп; `0x5aa018`/`0x5aa15b` — конец дорожки.
5. `0x5afb08`, `0x5afbe4`, `0x5afd8a` — затухания (от/до/мс).
6. `0x51fff7`, `0x521268`, `0x5b0472` — громкости.
7. Фон мест: `0x508e1a`/`0x508f42` (пауза), `0x509368` (вид, прорежённо).
Часто зовутся (не писать каждый вызов): `0x5afe61`, `0x52150e`, `0x508f9b`, `0x5202ea`, `0x520395`.
