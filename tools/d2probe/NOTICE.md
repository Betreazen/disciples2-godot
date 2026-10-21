# tools/d2probe — вендорная копия D2ModdingToolset

## Источник

- Форк: https://github.com/UnveN/D2ModdingToolset
- Скопирован из локального клона `disciples-modding/reference/D2ModdingToolset-unven`,
  HEAD `b57260c066d5f62ecad6bea452b72ac0e656eb67` (2025-12-07, "Update SLikeNet submodule").
- Лицензия: GPL-3.0-or-later (см. `LICENSE` в этой папке).
- Скопированы без изменений: `mss32/`, `lua/`, `buildDetours.bat`, `LICENSE`, а также
  `Examples/` — понадобилась дополнительно, см. «Правки» ниже.

## Сабмодули (ревизии зафиксированы форком-источником)

| Путь        | URL                                              | Коммит                                     | Ветка | Лицензия |
|-------------|--------------------------------------------------|--------------------------------------------|-------|----------|
| Detours     | https://github.com/microsoft/Detours.git         | `10ce1f4fb80f59ff799844682e1af9662de73450` | -     | MIT      |
| fmt         | https://github.com/fmtlib/fmt.git                | `415cd51913bd525e10943ac96c89ec7692f0e6be` | -     | MIT      |
| GSL         | https://github.com/microsoft/GSL.git             | `21b69e5ccedb036ea3c0c6275519cbe51a8dbc03` | -     | MIT      |
| sol2        | https://github.com/ThePhD/sol2.git               | `76065527f73542e8433d3997d74c7afb597bef79` | -     | MIT      |
| spdlog      | https://github.com/gabime/spdlog.git             | `27cb4c76708608465c413f6d0e6b8d99a4d84302` | v1.x  | MIT      |
| SLikeNet    | https://github.com/UnveN/SLikeNet.git            | `0fa35242518ca062ca8085f671487d9c8e52684b` | -     | MIT (`license.txt`); части, производные от RakNet, — BSD (`licenses/RakNet License.txt`) |
| D2RSG       | https://github.com/UnveN/D2RSG.git               | `f41b4303299ad1907fa3fa6c8bd1dc792c2c6391` | -     | GPL-3.0-or-later |

Плюс не сабмодуль, а скопированная в дерево `lua/` (см. «Источник» выше) —
Lua, MIT (текст лицензии в шапке `lua/lua.h`).

Всё перечисленное линкуется **статически** в одну `mss32.dll`, которая
распространяется под GPL-3.0-or-later (`LICENSE` в этой папке): MIT и BSD
совместимы с GPL-3 в одну сторону — их условия (сохранение копирайта и текста
лицензии) продолжают действовать, а результат целиком под GPL-3. D2RSG уже
GPL-3 и дополнительных условий не добавляет.

Все семь хешей нашлись в апстримах напрямую (без force-push, без нужды брать
ближайший коммит той же ветки) — это ровно те коммиты, что были зафиксированы
источником (`git ls-tree HEAD <submodule>` в `D2ModdingToolset-unven`).

## Правки (минимальные, логика не менялась)

1. **`mss32/mss32.vcxproj`, `SLikeNet/Lib/LibStatic/LibStatic.vcxproj`,
   `D2RSG/ScenarioGenerator/ScenarioGenerator.vcxproj`** — `PlatformToolset`
   `v142` → `v143` (единственный установленный тулсет — MSVC 14.44.35207,
   идентифицирует себя как v143). `WindowsTargetPlatformVersion` оставлен
   `10.0`, как и требовалось.

2. **`buildDetours.bat`** — `CALL VsDevCmd` заменено на
   `CALL "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat" -arch=x86`.
   Причина: `VsDevCmd` не на `PATH` в неинтерактивной оболочке, требовался
   полный путь под VS2022 Community; `-arch=x86` — чтобы `nmake` собирал
   Detours как x86 (иначе цель попадёт в `lib.X64`, а скрипт и
   `mss32.vcxproj` ждут `lib.X86`).

3. **`SLikeNet/Lib/LibStatic/LibStatic.vcxproj`** — для файла
   `IncrementalReadInterface.cpp` добавлен `<DisableSpecificWarnings>4100</DisableSpecificWarnings>`
   (unreferenced parameter `context` в `GetFilePart`). Проект собран с
   `WarningLevel=Level4` + `TreatWarningAsError=true`; более новый компилятор
   (14.44 против toolset'а, под который писался код) стал репортить это
   предупреждение, которого раньше не было. Исходный `.cpp` не тронут —
   правка только в опции компиляции для одного файла.

4. **`Examples/` вендорирована дополнительно** (не входила в список файлов
   брифа) — `mss32/mss32.rc` встраивает `..\Examples\Custom Lobby\CustomLobby.dlg`
   и `.ff` как ресурсы (`RCDATA`), без них ресурсный компилятор падает с
   `RC2135: file not found`. Скопирована целиком та же папка `Examples/`, что
   лежит в корне источника, без изменений.

## Сборка — параметр окружения, а не правка логики

`mss32.vcxproj` определяет `OutDir` как `$(DISCIPLES2_PATH)` — путь к папке
игры, куда автор оригинала подключал прямую пересборку. Если переменная не
задана, MSBuild падает с «BaseOutputPath/OutputPath не задано». Для локальной
пробной сборки (без установки в игру) это выставляется MSBuild-параметром
`-p:DISCIPLES2_PATH=Release\` — тогда `.dll` собирается прямо в
`mss32/Release/`. Проектный файл не менялся.

## Патчи сабмодулей D2RSG и SLikeNet — файлами, не гитлинком

Правки п. 1 (`v142`→`v143`) и п. 3 (`DisableSpecificWarnings` для
`IncrementalReadInterface.cpp`) физически лежат в файлах ВНУТРИ сабмодулей
`SLikeNet` и `D2RSG` — git отдельно трекает содержимое сабмодуля своим
коммитом, поэтому правка файла внутри него не попадает в историю внешнего
репозитория автоматически.

**Гитлинки указывают на апстримные ревизии** из таблицы выше — ровно на те,
что существуют в `github.com/UnveN/*`. Так `git submodule update --init` на
чистом клоне отрабатывает где угодно; указывать на локальный коммит, которого
в апстриме нет, нельзя — такой клон падал бы на `submodule update`.

Источник истины для правок — поэтому не гитлинк, а файлы патчей:

- `tools/d2probe/patches/D2RSG-0001-platformtoolset-v143.patch`
- `tools/d2probe/patches/SLikeNet-0001-platformtoolset-v143-suppress-c4100.patch`

и скрипт `tools/d2probe/apply-patches.sh`, который накладывает их на любое
состояние сабмодуля — патченное (сообщает «уже применён» и не трогает) или
чистый апстрим (накладывает). Порядок после
`git submodule update --init --recursive` — см. `README.md`, раздел «Сборка»,
шаг 0.

Следствие: после `apply-patches.sh` рабочие деревья `D2RSG`/`SLikeNet` всегда
«грязные» (`git status` во внешнем репозитории показывает `modified content`),
а `git submodule status` — чистый, без `+`. Это норма и признак того, что всё
на месте. Проверки, которым нужна «чистота исходников» (например расчёт
`ProbeGitHash` в `build.ps1`), должны звать git с
`--ignore-submodules=dirty`.

Оба `.patch`-файла и сам `apply-patches.sh` закреплены в корневом
`.gitattributes` как `text eol=lf` — иначе на машине с `core.autocrlf=true`
(это Windows-дефолт) git при чекауте переведёт их в CRLF, и `git apply`
может не найти совпадение контекста построчно. Целевые файлы внутри
`SLikeNet`/`D2RSG` этим правилом не покрыты — их перевод строк задаёт
собственная конфигурация тех репозиториев, вне нашего контроля; на момент
написания патчи накладывались чисто.

## Платформа солюшна: `x86`, не `Win32`

`mss32.sln`: `GlobalSection(SolutionConfigurationPlatforms)` объявляет
только `Debug|x86` / `Release|x86` — то есть на уровне solution платформа
называется `x86`. `ProjectConfigurationPlatforms` внутри того же `.sln`
маппит `Release|x86` (solution) → `Release|Win32` (каждый из трёх `.vcxproj`,
у которых `ProjectConfiguration` объявлены как `Win32`). Это не опечатка и
не расхождение с брифом — солюшн и проект называют одну и ту же платформу
по-разному на своих уровнях. Проверено: `MSBuild mss32.sln -p:Platform=Win32`
падает с `MSB4126` («указанная конфигурация решения "Release|Win32"
недопустима») — `Win32` МSBuild solution-уровню не принимает, только `x86`.
Поэтому в `README.md` везде `-p:Platform=x86` (для `mss32.sln`); если
кто-то соберёт один `mss32.vcxproj` напрямую (не через `.sln`), там,
наоборот, нужен `-p:Platform=Win32` — это работает независимо, но не тот
случай, что описан в README.

## Не выполнено этим агентом

Шаг 5 брифа (дымовая установка в `game-dev` и запуск игры) — по прямому
указанию контроллера задачи, выполняется отдельно.
