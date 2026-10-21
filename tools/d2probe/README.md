# d2probe

Вендорная копия community-тулсета [D2ModdingToolset](https://github.com/UnveN/D2ModdingToolset)
(форк UnveN), собранная без изменений логики под VS2022/v143 — прицепной
инструмент для зондирования движка Disciples 2 в рамках фазы Ф2 переноса в
Godot. Происхождение, ревизии сабмодулей и список правок под новый компилятор
— в [`NOTICE.md`](NOTICE.md).

## Сборка

Сборочная среда: VS2022 Community в стандартном пути установки (`C:\Program
Files\Microsoft Visual Studio\2022\Community\...`), единственный тулсет MSVC
14.44 (`v143`). Для другой редакции (Professional/Enterprise) или нестандартного
пути правится `VsDevCmd.bat` внутри `buildDetours.bat` — см. `NOTICE.md`.

0. После `git submodule update --init --recursive` наложить локальные патчи
   сабмодулей (v143 + подавление предупреждения, см. `NOTICE.md`) — без этого
   шага 2 упадёт на v142-тулсете/`C4100`:

   ```
   bash tools/d2probe/apply-patches.sh
   ```

   Идемпотентно: повторный запуск на уже патченных сабмодулях печатает
   «уже применён» и ничего не делает.

1. Собрать `Detours.lib` (нужен один раз — если уже есть `mss32/Lib/detours.lib`,
   шаг пропускается сам, через PreBuild-событие `mss32.vcxproj`):

   ```
   cd tools/d2probe/mss32 && cmd //c "..\buildDetours.bat ..\Detours"
   ```

2. Собрать решение — **после коммита исходников**, скриптом
   `tools/d2probe/build.ps1`:

   ```
   pwsh -File tools/d2probe/build.ps1
   ```

   Скрипт сам считает ревизию и передаёт её как `-p:ProbeGitHash` (см. «Ревизия
   зонда» ниже), зовёт MSBuild с нужными параметрами и в конце проверяет, что
   хеш лежит в собранной DLL байтами. Другая редакция VS — `-MSBuild <путь>`,
   отладочная сборка — `-Configuration Debug`.

   Что скрипт передаёт MSBuild и почему: `DISCIPLES2_PATH` подставляется как
   `OutDir` — при пробной сборке указывает прямо на `Release\`, чтобы `.dll`
   остался в дереве тулсета, а не уехал в папку игры; `Platform=x86`, а не
   `Win32` — так называется единственная платформа в
   `SolutionConfigurationPlatforms` `mss32.sln`, `Win32` он же для `mss32.sln`
   не принимается, хотя внутри каждого `.vcxproj` конфигурации называются
   `Release|Win32` — sln сам маппит `x86`→`Win32` построчно, см. `NOTICE.md`.
   Развёрнутая команда, если MSBuild зовётся руками, мимо скрипта (тогда и
   `ProbeGitHash` подставлять руками):

   ```
   "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" tools/d2probe/mss32/mss32.sln -p:Configuration=Release -p:Platform=x86 -p:DISCIPLES2_PATH="Release\\" -p:ProbeGitHash=<хеш> -m
   ```

   Результат: `tools/d2probe/mss32/Release/mss32.dll` (Win32/x86, ~5 МБ —
   статически слинкованы SLikeNet, D2RSG, Detours, Lua, fmt, spdlog).

3. Проверить, что это прокси-DLL с форвардами в `mss23`:

   ```
   "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207\bin\Hostx64\x86\dumpbin.exe" //exports tools/d2probe/mss32/Release/mss32.dll
   ```

   Ожидается 321 экспорт, все — `forwarded to mss23.*`.

## Режим зонда

DLL сама по себе — обычный тулсет. Режим зонда включается наличием каталога
`<игра>/probe/`: `probe::init` (вызывается из `DllMain` после определения версии
игры и до установки хуков) открывает в нём журнал `<yyyymmdd-hhmmss>-<pid>.jsonl`,
а `hooks::getHooks()` в этом случае возвращает только `getProbeHooks()`, тогда как
`getVftableHooks()` — пустой список. Игровые хуки тулсета не ставятся, и
`adjustGameRestrictions()` выходит сразу: движок должен считать по ванильным
формулам. Каждый хук зонда зовёт оригинал и пишет одну строку JSONL, ничего не
меняя. Нет каталога `probe/` — нет журнала, DLL работает как обычный тулсет.
В редакторе сценариев большинство этих адресов нулевые (`game.cpp`), записи с
нулевой целью в список не попадают: `DetourAttach` по нулю уронил бы `DllMain`.

### Отключение хуков поодиночке: probe/hooks.txt

Каждая запись в `getProbeHooks()` (`probehooks.cpp`) идёт под коротким именем —
им хук включается или выключается через `<игра>/probe/hooks.txt`, без пересборки
DLL. Нужно для бисекции: например, живой прогон показал, что при включённом
зонде Escape во вступительном ролике выходит из игры целиком, а без зонда (и на
тулсете 0.14.1) ролик просто пропускается — `hooks.txt` позволяет гасить хуки по
одному, пока поведение не сойдётся с 0.14.1.

Файла нет — ставятся все хуки, для которых движок вообще дал ненулевой адрес (как
без этого механизма). Файл есть — ставятся только хуки, чьи имена в нём
перечислены: по одному имени в строке, `#` и всё после него на этой строке —
комментарий, пробелы по краям обрезаются, пустые строки пропускаются. **Пустой
файл — ни одного хука**: это тоже рабочий случай, зонд пишет только `start` и
больше ничего — минимальный журнал для бисекции.

### Бисекция против полного тулсета: директивы `toolset:`

Живой прогон 05.09 показал, что переход «вступительный ролик → главное меню»
закрывал игру в режиме зонда, даже когда включена вся группа
`menu_ctor`…`menu_transition` (см. выше). Чтобы бисекция не упиралась в
пересборку DLL на каждый шаг, `probe/hooks.txt` понимает ещё и директивы —
строки с префиксом `toolset:`, вперемешку с именами хуков зонда, в любом
количестве и порядке:

| Директива | Действие |
|---|---|
| `toolset:A-B` | Добавить к хукам зонда элементы `hooks::getGameHooks()` с индексами `[A, B]` включительно (0-based, порядок — как в массиве `Hooks hooks{...}` и последующих `emplace_back` внутри `getGameHooks()`, `hooks.cpp`). |
| `toolset:all` | То же самое, но весь список целиком (эквивалент `toolset:0-(N-1)`, где `N` — фактический размер `getGameHooks()` в этом запуске). |
| `toolset:vftable` | `hooks::getVftableHooks()` возвращает список хуков тулсета вместо пустого (обычное поведение зонда). |
| `toolset:restrictions` | `adjustGameRestrictions()` (`main.cpp`) не делает ранний выход в режиме зонда — движок получает и правки `gameRestrictions`, которые тулсет обычно пишет в память. |

Без файла `hooks.txt` (или с файлом без единой директивы `toolset:`) поведение
не меняется — устанавливаются только хуки зонда, как было раньше.

Хук из `getGameHooks()`, чей `target` совпадает с адресом уже установленного
хука зонда, пропускается — зонд важнее; его индекс попадает в `toolset_skipped`
строки `start`. Хуки с нулевым `target` (движок не дал адрес) отбрасываются
тихо, как и у обычных хуков зонда. `toolset:A-B`/`toolset:all` читаются
**только** в игровом процессе (`executableIsGame()`) — как и восемь
battle-хуков и четыре экспериментальных menu-хука выше — `getGameHooks()`
документирован как «используется только в игре» (`hooks.h`); `toolset:vftable`
и `toolset:restrictions` не завязаны на это и применяются в любом процессе,
где включён зонд.

`getGameHooks()` строится ровно один раз за запуск: `getHooks()` в режиме
зонда до него не доходит вовсе (сразу отдаёт `getProbeHooks()`), так что
единственный вызов — из `getProbeHooks()`, когда указана директива диапазона.

Как узнать точное число элементов `getGameHooks()` в конкретной сборке —
номера рассчитаны для другого набора `userSettings()` (в частности для
`Scripts/settings.lua`, если он есть в `<игра>/probe/`) могут добавить или не
добавить условные хуки (`shatteredArmorMax`, `unrestrictedBestowWards`,
`debugMode` и т.п., см. `hooks.cpp`, конец `getGameHooks()`), поэтому точное
`N` — не константа исходников, а посчитать его надёжнее всего живым прогоном:

```
toolset:all
```

в `hooks.txt`, и в строке `start` журнала: `N = toolset_count + количество
элементов toolset_skipped` (плюс, теоретически, число хуков с нулевым
`target` — для Gog ожидается ноль, у него это игровой exe и адреса разрешены).
Статический подсчёт по исходникам (`hooks.cpp`, ветка Gog, `Scripts/settings.lua`
отсутствует → `userSettings() == defaultSettings()`): 139 хуков в фиксированном
списке `Hooks hooks{...}` плюс 17 из условных блоков (`sendRefreshInfoObjectCountLimit`
+1, `showBanners` +1, `showResources`/`showLandConverted` +1,
`unrestrictedBestowWards` +13, `movementCost.show` +1) — итого 156.

Полный список имён:

| Имя | Хук | Событие |
|---|---|---|
| `rng` | `generateRandomNumber` (`0x54851f`) | `rng` (только в бою); вне боя и в бою — `ai_rng` на броски решений игры (места — `probeairng.cpp`; схема 6.5, с 7.5 — все места вне показа и боя, поля `player`, `outer`, `origin`) |
| `rng_std` | `generateRandomNumberStd` (CRT `rand`, `0x548576`) | `rng_std` (только в бою); `ai_rng` — как у `rng` |
| `miss` | `attackShouldMiss` | `miss` |
| `dmg` | `computeDamage` | `dmg` — поле `crit` в нём это **аргумент** `computeCriticalHit`, а не результат вычисления критического удара; в журнале он всегда `true` |
| `dmg_max` | `computeDamageMax` | `dmg_max` |
| `dmg_buffs` | `computeDamageWithBuffs` | `dmg_buffs` |
| `armor` | `computeArmor` | `armor` |
| `power` | `getAttackPower` | `power` |
| `income` | `computePlayerDailyIncome` (`0x5da55f`) | `income` — с версии схемы 3 несёт ещё `turn` и `bank` (казна); на смене дня печатает вдобавок `counts` |
| `move_cost` | `computeMovementCost` (`0x6027f3`) | `move_cost` — **по ключу** `x:y:stack:флаги:ret` |
| `can_move` | `stackCanMoveToPosition` (`0x4cd1dd`) | `can_move` — **по ключу** `x:y:stack:ret` |
| `path` | `showMovementPath` (`0x4ccc2c`) | `path` — каждый вызов; `end_x`/`end_y` = клетка под курсором |
| `stack_move` | `CMidStackApi::get().setPosition` (`0x5ec917`) | `stack_move` — каждый вызов; `move_left` = остаток очков хода после шага |
| `xp` | `changeUnitXpCheckUpgrade` (`0x5d7412`) | `xp` — каждый вызов |
| `build_status` | `getBuildingStatus` (`0x5dc9e8`) | `build_status` — **по ключу** `player:building:ignore_cost:ret` |
| `hire_list` | `addPlayerUnitsToHireList` (`0x5d4911`) | `hire_list` — **по ключу** `player:a3:count:ret` |
| `site` | `updateEncLayoutSite` (`0x578b2b`) | `site` — **по ключу** id сайта |
| `immune` | `isUnitImmuneToAttack` (`0x65a528`) | `immune` — **по ключу** `unit:attack:class:source:always_only:ret` |
| `attack_new` | `createBatAttack` (`0x644b3e`) | `attack_new` — каждый вызов; одно боевое действие = одна строка, включая действия без урона |
| `attack_get` | `getAttackById` (`0x644626`) | `attack_get` — **по ключу** `impl:number:alt:attack:class:source` |
| `terrain_day` | **не хук**: обход карты внутри хука `income` (`0x5da55f`) | `terrain_day` — на каждый вызов `income`, т.е. раз на игрока в день; имени нет в `start.hooks`, но `probe/hooks.txt` его понимает (выключение обхода для бисекции) |
| `bank` | `CVisitorChangePlayerBank::apply` (`0x60be8d`) | `bank` — каждый вызов; единственная точка движения казны, несёт `delta`, `before`, `after` |
| `site_buy_item` | `CMidServerLogic::onSiteBuyItem` (`0x5d76e2`) | `site_buy_item` — каждый вызов |
| `site_sell_item` | `...onSiteSellItem` (`0x5d77f5`) | `site_sell_item` — каждый вызов |
| `site_buy_spell` | `...onSiteBuySpell` (`0x5d7952`) | `site_buy_spell` — каждый вызов |
| `site_buy_unit` | `...onSiteBuyUnit` (`0x5d7aa1`) | `site_buy_unit` — каждый вызов |
| `site_train` | `...onSiteTrainUnit` (`0x5d7c6b`) | `site_train` — каждый вызов |
| `item_equip` | `equipItem` (`0x5e741c`) | `item_equip` — каждый вызов; `apply = 0` — это вопрос «можно ли», а не надевание |
| `item_unequip` | `unEquipItem` (`0x5e746d`) | `item_unequip` — каждый вызов, та же оговорка про `apply` |
| `heal` | `healUnit` (`0x5d724e`) | `heal` — каждый вызов |
| `revive` | `reviveUnit` (`0x5d733f`) | `revive` — каждый вызов |
| `city_grow` | `growCity` (`0x5da01e`) | `city_grow` — каждый вызов |
| `build` | `CVisitorPlayerAddCapitalBuilding::apply` (`0x60c496`) | `build` — каждый вызов; факт постройки; цену снимает строка `bank` прямо перед ней (обе делает `0x5da121`) |
| `thief_roll` | `0x443067` | `thief_roll` — каждый вызов; треугольный бросок, оба кубика, порог, `rng_before`/`rng_after` |
| `thief_chance` | `0x5d31a1` | сама строка не пишется: хук запоминает порог для поля `chance` события `thief_roll` и тикает счётчик `counts.calls.thief_chance` |
| `relation` | `changeRelations` (`0x5d9366`) | `relation` — каждый вызов; схема 5.2: пара игроков, `value`/`absolute`, отношение до и после, адрес возврата и причина, все изменившиеся пары (третьи стороны) |
| `hire_unit` | `0x5d565f` | `hire_unit` — каждый вызов; схема 5.3: найм юнита — игрок, тип юнита, группа, позиция, казна до и после, `seq_end` |
| `hire_leader` | `0x5d6957` | `hire_leader` — каждый вызов; схема 5.3: найм лидера — игрок, укрепление, тип юнита, казна до и после, `seq_end` |
| `rod_drop` | `CEffectDropRod::apply` (`0x437606`) | `rod_drop` — каждое применение; схема 5.3: владелец и отряд, клетка, казна до и после, `seq_end` |
| `spell_cast` | `0x5dc2d6` (из `CEffectSpell::apply`) | `spell_cast` — каждый вызов; схема 5.3: игрок, заклинание, клетка, флаги второго кошелька и бесплатности, оба кошелька до и после, `seq_end` |
| `spell_research` | `0x5dc1d7` (из `CEffectSpellResearch::apply`) | `spell_research` — каждый вызов; схема 5.3: игрок, заклинание, флаг бесплатности, казна до и после, `seq_end` |
| `wand_cast` | `0x5dc4d6` (из `CEffectWand::apply`) | `wand_cast` — каждый вызов; схема 5.3: посох или свиток — игрок, цель, владелец и предмет, клетка, казна до и после, `seq_end` |
| `save` | `CMidServerLogicCore::saveGame` (`0x429f3f`) | `save` — каждое сохранение; схема 6.0: имя, временное ли, ход, успех, `seq_end` — точная граница «в файле / ещё нет» |
| `fort_capture` | `CEffectCapture::apply` (`0x433e2e`) | `fort_capture` — каждая попытка входа в чужую деревню; схема 6.0: владелец и визитёр до, владелец после, `seq_end` |
| `land_grow` | обход роста земли (`0x5e0551`) | `land_grow` — каждый обход; схема 6.0: центр, квота, террейн, клетки в порядке взятия |
| `land_reject` | приём клетки обходом (`0x5e07b2`) | `land_reject` — только на отказ, счётчик на каждый вызов |
| `site_visit` | вход в сайт (`0x5d7664`) | `site_visit` — каждый вызов; схема 6.0: сайт, отряд, владелец, очки хода на входе |
| `spell_expire` | `CEffectRemoveSpell::apply` (`0x43b9ef`) | `spell_expire` — раз на игрока в ход; схема 6.0 |
| `item_create` | создание предмета (`0x5e732f`) | `item_create` — каждый вызов; схема 6.0: ид-поля по типу, `apply`, адрес возврата и причина |
| `ap_effect` | конструктор `CEffectChangeAP` (`0x43588c`) | `ap_effect` — каждый; схема 6.0: объект, отряд, дельта, причина по адресу возврата |
| `ap_apply` | `CEffectChangeAP::apply` (`0x4358d4`) | `ap_apply` — каждое применение; очки хода до и после |
| `hp_change` | `CEffectChangeHP::apply` (`0x436299`) | `hp_change` — каждое применение; HP юнита до и после (общий с партией «лидеры, предметы, бой») |
| `garrison_max` | `CMidUnitGroupFortification` слот 5 (`0x6144ce`) | `garrison_max` — **по ключу** `fort:allowed:ret:ret_addr` |
| `scout` | `CUmStack::getScout` (`0x5a38e8`) | `scout` — **по ключу** `bonus:ret:ret_addr` |
| `rod_check` | проверка постановки жезла (`0x5d9e59`) | `rod_check` — **по ключу** `stack:x:y:flag:ret:ret_addr`; опрашивается курсором постановки |
| `hire_check` | проверка найма юнита (`0x5deea7`) | `hire_check` — **по ключу** `player:unit_type:group:position:ret:ret_addr`; опрашивается перетаскиванием |
| `hire_leader_check` | проверка найма лидера (`0x5de921`) | `hire_leader_check` — **по ключу** `player:unit_type:ret:ret_addr` |
| `merc_check` | проверка найма у наёмника (`0x5e00c6`) | `merc_check` — **по ключу** `site:stack:unit_type:ret:ret_addr` |
| `sell_check` | проверка продажи торговцу (`0x5dfe1a`) | `sell_check` — **по ключу** `site:item:ret:ret_addr`; опрашивается перетаскиванием |
| `grow_check` | проверка роста деревни (`0x5dc62a`) | `grow_check` — **по ключу** `player:fort:turn:ret:ret_addr` |
| `noble_action` | `CEffectNobleAction::apply` (`0x43a335`) | `noble_action` — каждое действие вора; схема 6.0: вор, игрок, цель, действие, успех, величина |
| `thief_cancel` | отмена действия вора (`0x443506`) | `thief_cancel` — каждая отмена |
| `noble_choose` | `CEffectNobleChoose::apply` (`0x43a5a7`) | `noble_choose` — каждый подход вора; доступные действия |
| `alliance_propose` | `CEffectProposeAlliance::apply` (`0x43b5c8`) | `alliance_propose` — каждое предложение союза |
| `alliance_change` | `CEffectChangeAlliance::apply` (`0x43574e`) | `alliance_change` — союз, разрыв, отказ; пара до и после |
| `trade_accept` | `CEffectAcceptTrade::apply` (`0x433805`) | `trade_accept` — ответ на обмен |
| `skill_offer` | список навыков лидера (`0x5d8b84`) | `skill_offer` — **по ключу** `leader:level:ids:ret`; схема 6.0, партия 2: лорд, уровень, предложенные `MODIF_ID` по порядку |
| `skill_pick` | `visitorLeaderUpgrade` (`0x5e84c2`) | `skill_pick` — выбор навыка (`apply = 1`): `upgCount` и счётчик `rng` до и после; вопросы `apply = 0` — только счётчик `skill_pick_query` |
| `upg_count` | `visitorLeaderUpgradeCount` (`0x5e8513`) | `upg_count` — каждый вызов: сколько выборов навыка доступно |
| `equip_check` | проверка надевания (`0x5df6b7`) | `equip_check` — **по ключу** `item:stack:slot:check_effect:ret:ret_addr`; опрашивается перетаскиванием |
| `wand_check` | проверка посоха или свитка (`0x5dbdd2`) | `wand_check` — **по ключу** `item:target:x:y:flag:ret:ret_addr`; опрашивается курсором |
| `potion_use` | `applyPotion` (`0x5d70f9`) | `potion_use` — каждое зелье, в бою и на карте; HP цели до и после |
| `item_consume` | расход боевого слота в конце боя (`0x629824`) | `item_consume` — 4 на бой, раньше `battle_end`; предмет слота до и после |
| `talisman_charge` | списание заряда талисмана (`0x5e74bb`) | `talisman_charge` — каждое списание |
| `xp_group` | `CBatAttackGroupUpgrade::upgradeGroup` (`0x665217`) | `xp_group` — 2 на бой: накопленный и применённый опыт группы, Инструктор |
| `weapon_master` | Инструктор у лидера группы (`0x665851`) | ничего своего — ответ в `xp_group.weapon_master`; счётчик `weapon_master` |
| `hit_<класс>` (20 имён) | `IBatAttack::onHit` классов без урона (`0x65ae85` … `0x6631eb`, `probehit.cpp`) | `attack_hit` — каждая цель действия: кто бьёт (по `attack_new`), цель, HP, статусы и модификаторы цели до и после; схема 6.1 |
| `rng_seed` | `setState` генератора боя под замком (`0x548473`, `probestate.cpp`) | `rng_seed` — каждый пересев и загрузка состояния: счётчик `n`, индекс до и после, аргумент, снимок 250 слов таблицы; схема 6.2 |
| `rng_reseed` | `reseed` (`0x5484bd`) | ничего своего — называет внешнюю причину в `rng_seed.cause`/`ret_addr` |
| `ap_restore` | `changeStackMove` (`0x5e780d`) — все изменения запаса хода, кроме создания отряда | `ap_restore` — каждый вызов: отряд, владелец, запас до и после, `MOVE` лидера, причина нуля, `cause` по адресу возврата (`turn_start` — начало хода); схема 6.2 |
| `unit_swap` | обмен клеток на сервере (`0x5d5642`, `CStackSwapUnitMsg`, ИИ `StackReorder`; `probeunit.cpp`) | `unit_transfer` — каждый вызов: группы и клетки, юниты до и после, `kind` swap/move, `cause`; схема 6.3 |
| `unit_dismiss` | увольнение на сервере (`0x5d5773`, `CStackDismissUnitMsg`) | `unit_transfer` с `kind = dismiss`; схема 6.3 |
| `swap_check` | проверка переноса интерфейсом (`0x5df4fb`) | `swap_check` по ключу: клетки, юниты, код (0, 1, 2, 3, `0xB`); схема 6.3 |
| `dismiss_check` | проверка увольнения (`0x5df266`) | `dismiss_check` по ключу: группа, клетка, юнит, код (0, 1, 2, 4, 5, 6); схема 6.3 |
| `equip_drop` | сброс предмета в слот героя (`CDDEquipment` слот 9, `0x504782`) | `equip_drop` по ключу: предмет, слот, статус (0 принят, 1 отказ с текстом, 2 молча); схема 6.3 |
| `ui_refusal` | `getInterfaceText` (`0x5c7f06`) | `ui_refusal` по ключу — только тексты отказов из перечня `probeunit.cpp`: id текста, имя, место; схема 6.3. С 7.0 тот же хук пишет `ui_text` по ключу (экран, id) — каждый id текста интерфейса |
| `wand_precheck` | калитки посоха и свитка (`0x5dbbe8`, `probecheck.cpp`) | `wand_precheck` по ключу: предмет, отряд, код (1, `0xE`, 9, `0x11`, `0x12`); схема 6.3 |
| `ai_break_check` | разрыв союза ИИ (`0x448897`, `probeai.cpp`) | `ai_break_check` — каждый вызов: крепости сторон, день, ход союза, d, T, шанс по модели, бросок, ответ; схема 6.4 |
| `ai_offer_check` | предложение союза ИИ (`0x448aad`) | `ai_offer_check` — каждый вызов: отношение, шанс, бросок, ответ; схема 6.4 |
| `ai_offer_chance` | шанс предложения (`0x448ae2`) | ничего своего — `ai_offer_check.chance` |
| `ai_gift_check` | подарок ИИ (`0x448bda`) | `ai_gift_check` — каждый вызов: порог, бросок, бросок вида, класс действия; схема 6.4 |
| `ai_answer_alliance` | ответ ИИ на союз (`0x474b37`) | `ai_answer` с `kind = alliance`: отвечающий, предлагающий, шанс, бросок, ответ; схема 6.4 |
| `ai_alliance_chance` | шанс ответа на союз (`0x474c21`) | в `ai_answer` (`rel`, `ret`); без `ai_answer_alliance` — своя строка без броска |
| `ai_answer_trade` | ответ ИИ на сделку (`0x474841`) | `ai_answer` с `kind = trade`; схема 6.4 |
| `ai_trade_chance` | шанс ответа на сделку (`0x4749c8`) | в `ai_answer` (`gold`, `price`, `ret`); без `ai_answer_trade` — своя строка |
| `ai_strategic_step` | запись памяти исполненного стратегического действия (`0x44c825`) | `ai_strategic_step` — каждое действие: цель, тип, день, номер прохода; схема 6.4 |
| `ai_pass` | полный сброс стратегии (`0x44e777`) | ничего своего — счётчик `ai_pass` и счётчики в `ai_turn` (конец) |
| `ai_turn_begin` | старт хода драйвера ИИ (`0x4719f8`) | `ai_turn` с `phase = begin`; схема 6.4 |
| `ai_turn_end` | `EndTurn` (`0x41e229`) | `ai_turn` с `phase = end`, `cause` `ai`/`human`; схема 6.4 |
| `ai_order` | смена приказа отряда (`0x5e811a`) | `ai_order` — каждый вызов: приказ, цель, приказы отряда до и после, `cause`; схема 6.4 |
| `ai_order_cmd` | команда `StackChangeOrder` ИИ (`0x41f21d`) | ничего своего — называет вызывающего в `ai_order.cause` |
| `ai_budget` | бюджет стратегического списка (`0x44661c`) | `ai_budget` — каждый список в начале хода: доля казны, резерв; схема 6.4 |
| `ai_chat` | сообщение ИИ (`0x4733a4`) | `ai_chat` — каждое: категория, бросок номера сообщения; схема 6.4 |
| `ai_eval` | добавление оценки стратегии (`0x44dcb8`, `probeaieco.cpp`) | `ai_eval` — каждая: построитель, объект по vftable; схема 6.5 |
| `ai_candidate` | добавление действия в список прохода (`0x44dd87`) | `ai_candidate` — каждое: номер в списке, объект, счёт; схема 6.5 |
| `ai_exec_heal`, `ai_exec_upgrade`, `ai_exec_hire_unit`, `ai_exec_hire_leader`, `ai_exec_capital`, `ai_exec_grow`, `ai_exec_spell` | исполнение действий экономики (`0x449fcb`, `0x44f3d2`, `0x44c6c6`, `0x44b09f`, `0x447d5c`, `0x4499d2`, `0x44d30c`) | `ai_exec` — каждое: объект, ответ; схема 6.5 |
| `ai_exec_dipl_break`, `ai_exec_dipl_offer`, `ai_exec_dipl_hate`, `ai_exec_dipl_item`, `ai_exec_dipl_spell`, `ai_exec_dipl_gold` | исполнение действий дипломатии (`0x449080`, `0x449100`, `0x449180`, `0x4491f7`, `0x449278`, `0x449307`) | `ai_exec`; `ai_exec_dipl_hate` (`CAiDiploHatePlayer`, vftable `0x6cf5c4`) — схема 6.5.1 |
| `ai_budget_spend` | списание бюджета (`0x4464ef`) | `ai_budget_spend` — бюджет и казна начала до/после; схема 6.5 |
| `ai_city_rank` | порядок городов (`0x44809c`) | `ai_city_rank` — список id и приоритетов; схема 6.5 |
| `ai_leader_role`, `ai_leader_role_fighter`, `ai_leader_role_thief` | роль и подприказ лидера (`0x44b5c0`, `0x44b6f3`, `0x44b7d0`) | `ai_leader_role` — двенадцать аргументов и ответ; схема 6.5 |
| `ai_leader_pick` | тип лидера (`0x44ad0f`) | `ai_leader_pick` — входы, ответ, бросок; схема 6.5 |
| `ai_unit_pick` | случайный юнит найма (`0x44c06a`) | `ai_unit_pick` — фильтр, длина списка, ответ; схема 6.5 |
| `ai_build_pick_s1`, `ai_build_pick_s2`, `ai_build_temple` | здание столицы (`0x44790a`, `0x446ea3`), храм (`0x5dcc28`) | `ai_build_pick`; схема 6.5 |
| `crt_rand` | `msvcrt!rand` (`GetProcAddress`) | `crt_rand` — каждое сырое значение с `tid`; схема 6.5 |
| `ai_tactic_candidate` | вставка кандидата тактики (`0x4686cf`, `probeaitac.cpp`) | `ai_tactic_candidate` — действие, счёт до/после, вставлено ли; схема 6.5 |
| `ai_tactic_adjust`, `ai_tactic_adjust_set` | поправки счёта (`0x466d5c`, `0x466cd8`) | `ai_tactic_adjust` — до/после, приоритет цели, id отряда по `CMidStack+0x18` (6.5.1); схема 6.5 |
| `ai_tactic_pick` | шаг тактики (`0x466388`) | `ai_tactic_pick` — фазы, топ-4, выбранное; `ai_tactic_stack` на смене отряда; схема 6.5 |
| `ai_tactic_result` | память результата (`0x461fb2`) | `ai_tactic_result`; схема 6.5 |
| `ai_rod_cell` | клетка жезла (`0x46164d`) | `ai_rod_cell` — восемь соседей с пере-проверкой; схема 6.5 |
| `ai_strength` | отношение сил (`0x5dda25`) | `ai_strength` — A, B, C, ответ, длины векторов юнитов `p2_n`/`p4_n` (6.5.1); схема 6.5 |
| `ai_tactic_enter`, `ai_cache`, `ai_cache_forts`, `ai_cache_places`, `ai_fort_filter`, `ai_fort_guard`, `ai_cache_pick` | вход в тактику (`0x46586c`, `probeai72.cpp`), кэш целей (`0x45e4c7`), сбор крепостей и мест (`0x45eb14`, `0x45efa4`), фильтр и охрана крепости (`0x45ec77`, `0x45ed86`), отбор трёх (`0x45f793`) | `ai_place_cache` (`enter` — кошелёк тактики и казна; `cache` — площадки с причиной отбора) и `ai_fort_cache` (крепости с фильтром, охраной и причиной); схема 7.2 |
| `ai_strength_group`, `ai_strength_unit` | сила группы (`0x5dcec7`) и юнита (`0x5dd06c`) | `ai_strength_units` — вклад каждого юнита: здоровье, сила, модификаторы; схема 7.2 |
| `regen`, `regen_fort_term`, `regen_unit_term` | лечение процентом (`0x5d4099`), петли слагаемых укрепления (`0x5d4181`) и юнита (`0x5d4588`) | `regen` — юнит не на полном здоровье: слагаемое, хозяин группы, процент, здоровье до/после; схема 7.2 |
| `upgrade_target`, `upgrade_leader` | цель повышения (`0x5d7fe7`, лидер — `0x5d89c6`) | `upgrade_target` — цель или null и запросы движка по пути; схема 7.2 |
| `path_expand`, `path_expand_node` | искатель пути (`0x5cc6da`), раскрытие узла A* (`0x680786`) | `path_expand` — только отряды не людей: порядок снятых узлов (до 300), путь; схема 7.2 |
| `id_alloc` | новый id объекта сценария (`0x5ee8bd`, `CMidgardScenarioMap` слот 9) | `id_alloc` — тип, id, счётчик, адреса вызывающих; схема 7.2 |
| `snd_play`, `snd_loop`, `snd_sounds` | `CWavStore::play` (`0x56731a`), `playLoop` (`0x5674ce`), `Sounds::playByName` (`0x5b03e9`, только причина) — `probeav.cpp` | `snd` — каждый звук: имя, архив и `via` по адресу возврата, громкость, панорама, номер, `ok`; схема 7.3 |
| `snd_eos`, `ail_end_sample`, `ail_start_sample`, `ail_sample_volume`, `ail_sample_pan` | колбэк конца сэмпла (`0x5205d9`, поток Miles); ячейки импорта Miles (`0x6cc4cc`, `0x6cc4c4`, `0x6cc4d8`, `0x6cc4c8`) | `snd_end` — конец или снятие звука: мс, громкость от и до, смен; схема 7.3 |
| `music_start`, `music_fade_out`, `music_fade_then`, `music_fade_in`, `music_end_main`, `music_end_temp`, `ail_open_stream`, `ail_start_stream`, `ail_close_stream`, `ail_pause_stream`, `ail_stream_volume`, `ail_stream_position` | старт дорожки `0x67cbb1`, `CMusicFader` (`0x5afb08`, `0x5afbe4`, `0x5afd8a`), конец дорожки (`0x5aa018`, `0x5aa15b`); ячейки импорта потоков Miles | `music` — `open`/`start`/`stop`/`pause`/`resume`/`seek`/`end`/`fade`/`volume` (шаги громкости — сводкой); схема 7.3 |
| `vol_fx`, `vol_music`, `vol_voice` | `0x51fff7`, `0x521268`, `0x5b0472` | `vol_set`; схема 7.3 |
| `bink_open`, `bink_close`, `bink_frame` | ячейки импорта Bink (`0x6cc44c`, `0x6cc440`, `0x6cc42c`) | `video` — `open` (кадры, fps), `close` (показано кадров, мс); схема 7.3 |
| `map_anim_tick`, `map_anim_end` | тик очереди анимаций карты `0x40ae5f`, снятие законченной `0x40b355` — `probeanim.cpp` | `map_anim` — класс RTTI, мс, тики, кадры, клетка; схема 7.3 |
| `frames` | конец кадра `CRendererImpl` `0x5179f0` | `frames` — раз в 10 с: кадров, самый долгий, тики групп аниматора; схема 7.3 |
| `cursor`, `cursor_map`, `cursor_battle` | показ курсора `0x532f69`; создание курсоров карты `0x5b27ee` и боя `0x5b345e` (имена) | `cursor` — на смену; схема 7.3 |
| `view`, `view_center` | смена центра вида `0x540d37`; центрирование `0x540c38` (причина) | `view` — серия прокрутки одной строкой; схема 7.3 |
| `bat_anim`, `bat_anim_idle`, `bat_anim_swing`, `bat_anim_hurt`, `bat_anim_item` | `addUnitImage` `0x646ec5`; методы `CBatUnitAnim` (`0x6537f6`, `0x654346`, `0x653fb7`, `0x651c9c`) — только юнит — `probebat.cpp` | `bat_anim` — вид, юнит, тип, кадры; строка на смену; `bat_fx` `HEFF`; схема 7.3 |
| `bat_effect`, `bat_fx`, `bat_show`, `bat_show_end`, `bat_text` | `playEffect` `0x65255b`, картинка на месте `0x636239`, шаг просмотра `0x630a7b`, конец шага `0x638183`, надпись `0x64d09c` | `bat_effect`, `bat_fx` (`TUCH`/`TICK`/`HEAL`), `bat_show`, `bat_show_end`, `bat_text`; схема 7.3 |
| `ui_open` | воронка «диалог по имени из `.dlg`» (`0x5c8110`, `probeui.cpp`) | `ui_open` — каждый диалог: имя, адрес, класс и вид хозяина (RTTI), верхний экран, место вызова; схема 7.0; 7.1 — адрес хозяина `parent`, `top_class`, `base` у `DLG_BATTLE_B` |
| `ui_close` | деструктор `CDialogInterf` (`0x50b6c6`) | `ui_close` — каждый: имя, адрес; схема 7.0 |
| `ui_show`, `ui_remove`, `ui_hide` | стек экранов `CInterfManagerImpl` (слоты 4–6: `0x53cac4`, `0x53cb64`, `0x53cbd1`) | `ui_show`/`ui_show_refused`/`ui_hide` — экран, класс, вид, глубина, верхний; схема 7.0; 7.1 — адрес экрана `iface`, `top_class`, `seq` берётся до вызова оригинала. Хук `ui_clear` (слот 7 `0x53ccbf`) с 7.1 снят: движок эту функцию не зовёт |
| `ui_button`, `ui_toggle`, `ui_lbox` | обработчики кнопки (`0x5318e1`), переключателя (`0x534ba3`), смены выбора в списке (`0x535f0c`) | `ui_click` — каждое: диалог и имя элемента из `.dlg`, горячая клавиша / состояние / строка; схема 7.0; 7.1 — переключатель одной строкой, `lbox.what` = `select`/`scroll`, у безымянного элемента `child` и `area` |
| `ui_msgbox` | конструктор `CMidgardMsgBox` (`0x5c7771`) | `ui_msgbox` — текст окна, последний id текста, «да/нет»; схема 7.0 |
| `game_enter`, `game_leave`, `game_start` | конструктор и деструктор `CPhaseGame` (`0x40529f`, `0x40566c`); открытие сценария сервером (`0x429603`, схема 7.1) | `game_phase` — 7.0: `enter`/`leave`; 7.1: `new`/`load` (до конструктора, раньше заставки) и `leave` |
| `fort_day` | **не хук**: обход укреплений внутри хука `income` (`0x5da55f`) | `fort_day` — на каждый вызов `income`; имени нет в `start.hooks`, но `probe/hooks.txt` его понимает, как и `terrain_day` |
| `input` | `PeekMessageA` (экспорт `user32.dll`) | `input` — только на нажатие кнопки или клавиши, никогда на движение мыши |
| `input_get` | `GetMessageA` (экспорт `user32.dll`) | `input` — то же событие с другого входа |
| `menu_ctor` | `CMenuPhaseApi::constructor` | ничего — эксперимент, выключен по умолчанию |
| `menu_dtor` | `CMenuPhaseApi::vftable()->destructor` | ничего — эксперимент, выключен по умолчанию |
| `menu_switch_phase` | `CMenuPhaseApi::switchPhase` | ничего — эксперимент, выключен по умолчанию |
| `menu_transition` | `CMenuPhaseApi::transitionToMainOrCloseGame` | ничего — эксперимент, выключен по умолчанию, не включать вместе с `menu_force_main` (тот же адрес) |
| `menu_force_main` | `CMenuPhaseApi::transitionToMainOrCloseGame` | `menu_force_main` — эксперимент, выключен по умолчанию, не включать вместе с `menu_transition` (тот же адрес) |
| `round` | `BattleMsgDataApi::beforeBattleRound` (`0x621fd0`) | `battle_start` (первый вызов на ещё не объявленный `BattleMsgData*`) плюс `round` на каждый вызов |
| `turn_before` | `beforeBattleTurn` (`0x62673f`) | `turn_before` |
| `turn_after` | `afterBattleTurn` (`0x626609`) | `turn_after` |
| `battle_turn` | `CBatLogic::battleTurn` (`0x62481a`) | `battle_units` (первый вызов на бой) либо `battle_turn` (дальше) |
| `battle_end` | `updateGroupsIfBattleIsOver` (`0x62953c`) | `battle_end` |
| `bmd_ctor` | `BattleMsgData` конструктор (`0x62c920`) | ничего — только сбрасывает память о прошлом бое |
| `auto_battle` | `BattleMsgDataApi::isAutoBattle` (`0x622f58`) | `autobattle_forced` |
| `fast_battle` | `BattleMsgDataApi::isFastBattle` (`0x622f71`) | `autobattle_forced` |

`menu_ctor`, `menu_dtor`, `menu_switch_phase` и `menu_transition` — хуки
тулсета вокруг `CMenuPhase` (`menuphasehooks.cpp`), взятые как есть; в режиме
зонда они не журналируют ничего. **По умолчанию они не ставятся** — как и
`menu_force_main`, каждый включается только явным именем в `probe/hooks.txt`
(отсутствие файла, которое для остальных хуков значит «включены все», для этих
четырёх значит «выключены»).

Изначально они были добавлены как «группа совместимости»: при включённом зонде
переход «вступительный ролик → главное меню» закрывал игру, а с полным
тулсетом 0.14.1 — нет. Бисекция 05.09 это обоснование опровергла: причиной
выхода были **модовые таблицы Globals** в `game-dev` (`Gattacks`, `Gunits`,
`Gmodif`, `GUmodif.dbf`, `LModifS`, `Gbuild`/`GbuiList`), которых чистый движок
на входе в меню не ждёт; с эталонными таблицами
(`install.py --vanilla-globals`) игра доходит до меню и без единого menu-хука.
Корпус снимается без них — группа остаётся только для экспериментов.
`menu_ctor` попутно зовёт `loadScenarioTemplates()`, которая молча выходит,
если `Templates/` нет — отсутствие каталога в режиме зонда не роняет игру.

`auto_battle` и `fast_battle` ставятся вообще только при маркере
`probe/autobattle` (см. «Автобой» ниже) — без него `hooks.txt` их не касается,
ставить нечего. Все восемь battle-хуков (`round`…`fast_battle`) и все четыре
экспериментальных menu-хука (`menu_ctor`…`menu_transition`) не читаются в
редакторе сценариев независимо от `hooks.txt` (см. выше).

Итоговый список реально установленных имён — после фильтра `hooks.txt` и
проверки на нулевой адрес — попадает в саму строку `start`, поле `hooks`.

Когда `hooks.txt` содержит хотя бы одну директиву `toolset:` (см. выше), в
`start` добавляются ещё пять полей — без них строка `start` выглядит ровно так
же, как без `hooks.txt`:

| Поле | Что это |
|---|---|
| `toolset_range` | `"A-B"` или `"all"` для `toolset:A-B`/`toolset:all`; `null`, если ни одна из двух директив не указана. |
| `toolset_count` | Сколько хуков `getGameHooks()` реально добавлено (после фильтра конфликтов и нулевого `target`). |
| `toolset_skipped` | Индексы `getGameHooks()`, пропущенные из-за конфликта `target` с уже установленным хуком зонда. |
| `toolset_vftable` | `true`/`false` — указана ли `toolset:vftable`. |
| `toolset_restrictions` | `true`/`false` — указана ли `toolset:restrictions`. |

Поля, общие для всех строк:

| Поле | Что это |
|---|---|
| `seq` | Монотонный номер события, снимается **до** вызова оригинала. Строки вложенных вызовов ложатся в файл раньше внешнего (`dmg` внутри себя зовёт `dmg_buffs`, `armor`, `power`), поэтому файл по `seq` не отсортирован — по нему восстанавливается вложенность. |
| `tid` | `GetCurrentThreadId()` потока, из которого пришёл вызов. |
| `t` | **Схема 5.** Миллисекунды с открытия журнала (`GetTickCount64`). Стена времени этого нуля — в имени файла. В журналах схем 2.1–4 поля нет. |
| `ev` | Тип события: `start`, `rng`, `rng_std`, `miss`, `dmg`, `dmg_max`, `dmg_buffs`, `armor`, `power`, `income`, `round`, `turn_before`, `turn_after`, `battle_start`, `battle_units`, `battle_turn`, `battle_end`, `autobattle_forced`, `move_cost`, `can_move`, `path`, `stack_move`, `xp`, `build_status`, `hire_list`, `site`, `counts`, `immune`, `attack_new`, `attack_get`, `terrain_day`, `bank`, `site_buy_item`, `site_sell_item`, `site_buy_spell`, `site_buy_unit`, `site_train`, `item_equip`, `item_unequip`, `heal`, `revive`, `city_grow`, `build`, `thief_roll`, `fort_day`, `input`, `relation`, `hire_unit`, `hire_leader`, `rod_drop`, `spell_cast`, `spell_research`, `wand_cast`, а со схемы 6.0 ещё `save`, `fort_capture`, `land_grow`, `land_reject`, `site_visit`, `spell_expire`, `item_create`, `ap_effect`, `ap_apply`, `hp_change`, `garrison_max`, `scout`, `rod_check`, `hire_check`, `hire_leader_check`, `merc_check`, `sell_check`, `grow_check`, `noble_action`, `thief_cancel`, `noble_choose`, `alliance_propose`, `alliance_change`, `trade_accept`, а с партии 2 — `skill_offer`, `skill_pick`, `upg_count`, `equip_check`, `wand_check`, `potion_use`, `item_consume`, `talisman_charge`, `xp_group`, а со схемы 6.1 — `attack_hit`, со схемы 6.2 — `rng_seed`, `ap_restore`, со схемы 6.3 — `unit_transfer`, `swap_check`, `dismiss_check`, `equip_drop`, `ui_refusal`, `wand_precheck`, со схемы 6.4 — `ai_break_check`, `ai_offer_check`, `ai_gift_check`, `ai_answer`, `ai_strategic_step`, `ai_turn`, `ai_order`, `ai_budget`, `ai_chat`, со схемы 6.5 — `ai_eval`, `ai_candidate`, `ai_exec`, `ai_budget_spend`, `ai_city_rank`, `ai_leader_role`, `ai_leader_pick`, `ai_unit_pick`, `ai_build_pick`, `ai_rng`, `crt_rand`, `ai_tactic_candidate`, `ai_tactic_adjust`, `ai_tactic_pick`, `ai_tactic_stack`, `ai_tactic_result`, `ai_rod_cell`, `ai_strength`, со схемы 7.0 — `ui_open`, `ui_close`, `ui_show`, `ui_show_refused`, `ui_hide`, `ui_clear` (только 7.0), `ui_click`, `ui_text`, `ui_msgbox`, `game_phase`. |

Восемь хуков карты и экономики (`move_cost`…`site`) добавлены схемой корпуса 3
(`docs/corpus-schema.md`, раздел «События карты»), три хука магии и предметов
(`immune`, `attack_new`, `attack_get`) плюс обход карты `terrain_day` — схемой 4
(там же, раздел «События магии, предметов и земли»). Семь из них — `move_cost`,
`can_move`, `build_status`, `hire_list`, `site`, `immune`, `attack_get` — пишутся
**по ключу**: строка на новое сочетание признаков, а не на каждый вызов. `computeMovementCost` зовётся
поиском пути тысячи раз за одно движение мыши; запись каждого вызова сделала бы
журнал бесполезным, а бюджет «первые N строк» запрещён правилом репозитория —
частый случай выест бюджет и спрячет редкий. Поэтому рядом с ключом стоит
счётчик **без бюджета и без потолка** (`probe::countCall`), и он печатается
событием `counts` — на смену дня (хук `income`) и один раз при выгрузке DLL
(`DllMain`, `DLL_PROCESS_DETACH`). Из этого следует правило чтения: у keyed-события
число строк — это число уникальных сочетаний, а число вызовов лежит только в
`counts.calls`; «строки нет» и «вызова не было» различаются только по `counts`.

Все сто восемь счётчиков (`move_cost`, `can_move`, `path`, `stack_move`, `xp`,
`build_status`, `hire_list`, `site`, `income` — схема 3; `immune`, `attack_new`,
`attack_get`, `terrain_day` — схема 4; `bank`, `site_buy_item`, `site_sell_item`,
`site_buy_spell`, `site_buy_unit`, `site_train`, `item_equip`, `item_unequip`, `heal`,
`revive`, `city_grow`, `build`, `thief_roll`, `thief_chance`, `fort_day`, `input` — схема 5;
`relation` — схема 5.2; `hire_unit`, `hire_leader`, `rod_drop`, `spell_cast`,
`spell_research`, `wand_cast` — схема 5.3; двадцать четыре имени схемы 6.0, партия 1 —
`save` … `trade_accept`; одиннадцать имён партии 2 — `skill_offer` … `weapon_master`, включая
`skill_pick_query`; `attack_hit` — схема 6.1; `rng_seed`, `ap_restore` — схема 6.2; шесть имён схемы 6.3 — `unit_transfer` … `wand_precheck`; десять имён схемы 6.4 — `ai_break_check` … `ai_chat`; восемнадцать имён схемы 6.5 — `ai_eval` … `ai_strength`; `ui_text` — схема 7.0; полный список в `docs/corpus-schema.md`)
заводятся нулями при старте, поэтому
в `counts.calls` они присутствуют всегда — `"move_cost":0` читается как «хук стоял
и не звался», а «встал ли хук» видно в поле `hooks` строки `start`. Ключи у каждого
вида строки свои, со своим потолком (схема 7.4): `move_cost` — 200 000 (с 7.5 — 1 000 000:
ключ хранится 64-битным хешем FNV-1a, а не строкой, память та же), прочие виды —
по 20 000; при входе в партию (хук `game_enter`, строка `game_phase`) все ключи
забываются, и каждое сочетание на новой карте снова даёт строку. Отброшенные сверх
потолка ключи считают `counts.keys_dropped` (всего за прогон) и `counts.keys_dropped_by`
(по видам, только виды с отказами), сбросы — `counts.keys_resets` (`calls` при этом
точны). До 7.4 множество было одно на прогон с общим потолком 200 000, и в
`corpus/20261016e-av` его к карте 5 заняли ключи `move_cost` — на картах 6–7 нет ни
одной строки `*_check` (`docs/probe-av.md`, раздел 6). Финальная строка `counts` берёт
замки без ожидания (`try_to_lock`) и при занятом замке не пишется: выход из игры —
`ExitProcess`, прочие потоки к моменту `DllMain` уже убиты, и блокирующее ожидание
повесило бы выход.

`rng` и `rng_std` пишутся в журнал только пока бой активен — от строки `battle_start`
до строки `battle_end` включительно (флаг ставит/снимает `probe::setBattleActive()`,
`probehooks.cpp`); вне боя (ИИ на карте, доход, и т.п.) это шум — за реальный прогон
50 714 строк `rng`/`rng_std` против 19 внутри боя. Счётчики `n` (и, значит,
`rng_before`/`rng_after` у `miss`/`dmg`) продолжают расти и вне боя — режется только
сама строка, не нумерация.

`rng` и `rng_std` считаются раздельными счётчиками `n`; в `rng_before`/`rng_after`
событий `miss` и `dmg` входит только `rng` (движковый генератор `0x54851f`), но не
`rng_std` (CRT `rand`, `0x548576`). Инкремент счётчика и запись строки идут под тем
же мьютексом журнала, поэтому `n` совпадает с порядком строк.

Журнал открыт в бинарном режиме: текстовый режим CRT превращает `\n` в `\r\n`, и
`\r` попадает внутрь разбираемых строк JSONL.

### Снимки состояния боя

| Событие | Точка | Поля сверх общих |
|---|---|---|
| `battle_start` | `BattleMsgDataApi::beforeBattleRound` (`0x621fd0`), первый вызов на ещё не объявленный `BattleMsgData*` | `attacker_group`, `defender_group`, `attacker_player`, `defender_player`, `round`, `units` (без опознания) |
| `round` | он же, каждый вызов | `round`, `order`, `units` |
| `battle_units` | `CBatLogic::battleTurn` (`0x62481a`), первый вызов на бой | `order`, `units` (с опознанием) |
| `battle_turn` | он же, последующие вызовы | `round` |
| `turn_before` | `beforeBattleTurn` (`0x62673f`) | `unit`, `round`, `order`, `units` |
| `turn_after` | `afterBattleTurn` (`0x626609`) | `unit`, `next`, `round`, `order`, `units` |
| `battle_end` | `updateGroupsIfBattleIsOver` (`0x62953c`), когда после оригинала `isBattleOver` (`0x629f1e`) | `winner` (`getBattleWinnerGroupId`, `0x62a08f`), `round`, `units` |

`battle_start` и `battle_units` — два разных события об одном и том же начале боя,
разнесённые по двум точкам потому, что ни одна из них не даёт всего сразу:
`beforeBattleRound` получает `BattleMsgData*` без `objectMap` (группы/игроки уже
известны, опознание юнитов недоступно), а `battleTurn` получает `CBatLogic*` — и
`battleMsgData`, и `objectMap` разом. Живой прогон 05.09 показал первый `round`
(и значит `battle_start`) на seq 52975, а прежний `battle_start` при `battleTurn`
(теперь `battle_units`) — на seq 52985, т.е. позже: писать оба под одним именем в
одной точке нельзя было бы, не потеряв, что раньше на самом деле известно.

`units` — занятые слоты `BattleMsgData::unitsInfo[22]`: пустой слот опознаётся по
`unitId1 == emptyId` **или** `unitId1 == invalidId` — `resetUnitInfo()`
(`battlemsgdatahooks.cpp`) сбрасывает пустой слот именно в `invalidId`, поэтому
проверка только на `emptyId` (как было раньше) слот не находит и пишет его как
обычный юнит с `id:"INVALID-ID"` (так журнал 05.09 получил 22 юнита вместо 2 занятых
слотов в каждом снимке боя). Поля слота: `id`, `hp`, `xp`, `st`/`st2` (битовые маски
статусов), `flags` (байт `UnitFlags`), `shattered`, `fort`. В `battle_units` набор
другой: только `id` плюс, если юнит опознан, `type` (id импла из `Gunits`), `level`
и `hp_max` — по объекту сцены через `findUnitById` и `IUsSoldier`; юнит, которого нет
в карте объектов, идёт без этих трёх полей, а не с выдуманными (`battle_start` тоже
пишет `units`, но без опознания — `objectMap` в его точке недоступен). `order` —
`turnsOrder[13]` до первого пустого/invalid `unitId` (тот же критерий, что и для
слотов `unitsInfo`; старый журнал 05.09 останавливался только на `emptyId` и получал
`order` длиной 13 с хвостом `"INVALID-ID"`): `id` и `attacks`.

`order` пишется из четырёх событий — `round`, `turn_before`, `turn_after`,
`battle_units` — и заполнен только там, где движок его к этому моменту уже
построил; где ещё нет, `turnsOrder` состоит из invalid-id и поле выходит пустым
(`"order":[]`). В корпусе 06.09 (`corpus/20260906`) пуст во **всех** раундах:
`beforeBattleRound` очередь хода не строит. Гадать, где именно строит, не стали —
поле пишется во всех четырёх точках, и следующий живой прогон покажет, в какой
из них оно непусто.

«Новый бой» = вызов `beforeBattleRound`/`battleTurn` с другим `BattleMsgData*`, чем у
боя, который уже дал `battle_start`/`battle_units`. Память об этих указателях
сбрасывает **конструктор** `BattleMsgData` (`0x62c920`, хук ничего не пишет в
журнал): движок переиспользует память, и построение структуры поверх запомненной —
это и есть конец прошлого боя. `battle_end` память не трогает, иначе доигровка
лекарями после конца боя (`afterBattle`) читалась бы как второй бой. Повторный
`battle_end` отсекается отдельной памятью «бой, о конце которого уже написано»:
`updateGroupsIfBattleIsOver` движок зовёт не один раз. `beforeBattleRound` для
раунда 1 вызывается дважды подряд — на второй раз `BattleMsgData*` уже не новый,
поэтому `battle_start` не повторяется, а `round` пишется на каждый из двух вызовов.

`battle_start`, `battle_units` и `battle_turn` пишутся **до** вызова оригинала — они
описывают состояние, с которого ход начинается; остальные четыре — после, они
описывают то, что оригинал сделал. `updateGroupsIfBattleIsOver` — единственный хук,
который снимает `seq` после оригинала: будет ли строка вообще, решает состояние
после вызова, а взятый впустую `seq` выглядел бы для проверки журнала как потерянная
строка.

### Автобой

Если рядом с журналом лежит файл-маркер `<игра>/probe/autobattle`, зонд отвечает
`true` на оба вопроса движка про режим боя: хуки `BattleMsgDataApi::isAutoBattle`
(`0x622f58`) и `isFastBattle` (`0x622f71`) возвращают `true`, не зовя оригинал —
единственные два хука зонда, которые так делают. Без маркера они вообще не ставятся.
На первый такой ответ в каждом бою пишется `{"ev":"autobattle_forced"}`; наличие
маркера видно и в строке `start` (`"autobattle":true`).

Дедуп — по указателю `BattleMsgData*` (`firstTimeSeen(announcedBattle, thisptr)`), а не
по бою: живой прогон 05.09 показал `autobattle_forced` дважды на бой, потому что
структура реплицируется (минимум две копии `BattleMsgData` с разными адресами на одну
и ту же логическую битву), и по адресу это не одна и та же запись. Дедуп по значению
(например, по паре `attackerGroupId`/`defenderGroupId`) закрыл бы этот случай, но
завёл бы новый: те же две группы могут сойтись в бою повторно в одном сценарии
(отступление и повторная атака), и тогда второй настоящий бой был бы молча проглочен.
Простого способа различить «та же битва, другая копия» от «новая битва тех же групп»
без просмотра дополнительных полей нет — оставлено как есть, двойная запись на бой
безопаснее пропущенной.

Отвечать читателям, а не писать биты `autoBattle`/`fastBattle` в
`BattleMsgData::battleStateFlags` — сознательный выбор: структура ходит по сети и
копируется целиком, копия затёрла бы байт обратно, и пришлось бы гадать, успели ли
выставить флаг до первого чтения. Прогон без человека иначе встаёт на экране боя.
Формулы остаются ванильными: меняется, кто выбирает действие, а не как оно считается.

### Броски решений игры: ai_rng (схема 7.5)

Строк `rng` вне боя нет (это ~1,4 млн бросков грунта карты за партию), поэтому значения бросков ИИ и событий пишет `ai_rng` — по адресу
возврата из обёртки генератора, только для мест из таблицы `probeairng.cpp`: все прямые вызовы `0x54851f`/`0x548576` в решениях игры (30
мест R250 и 5 CRT; показ и бой — нет). У перемешиваний и общих выборов (`0x4495d8`, `0x43949e`, `0x46e92c`, `0x46fe5e`, `0x435f57`)
`outer` — цепочка адресов возврата из обёрток (просмотр стека вверх с проверкой `E8 rel32` → начало обёртки), `origin` — имя места
(`mage`, `gift_spell`, `harass_far` …). Таблица мест и полей — `docs/corpus-schema.md`, «Схема 7.5»; броски порту — `JournalRolls`.

### Покрытие кода: probe/coverage.txt (схема 7.3)

Файл `<игра>/probe/coverage.txt` (кладёт `install.py --coverage` из `coverage/functions.tsv`) включает запись того, какие функции
`Discipl2.exe` исполнились (`probecov.cpp`): в `DllMain` после установки хуков на каждое начало из списка ставится одноразовая точка
останова `0xCC`, обработчик VEH отмечает первое попадание (порядок, `t`, `seq`) и возвращает байт. Итог — `<журнал>.cov.txt` рядом с
журналом (переписывается на смене дня и при выходе) и поле `coverage` строк `counts`; первая строка журнала после `start` —
`coverage` (сколько точек поставлено). Строка `# exe_size N` списка сверяется с размером exe — список для другой сборки не ставится.
Свод по областям и неисполненные функции логики — `py tools/probe_coverage.py report <каталог probe>`; `docs/probe-av.md`, раздел 4.

### Ревизия зонда в строке `start`

Ревизия зонда попадает в строку `start` (`{"ev":"start",...,"probe":...}`) из define
`PROBE_GIT_HASH`. По умолчанию — `dev`; настоящий хеш подставляет
`tools/d2probe/build.ps1` (`-p:ProbeGitHash=<хеш>`).

Хеш обязан указывать на код, из которого собрана DLL, поэтому:

- **собирать только после коммита исходников** — сборка «до коммита» запишет в
  журнал предыдущую ревизию, и корпус будет ссылаться не на тот код;
- если в `tools/d2probe` остались незакоммиченные правки
  (`git status --porcelain --ignore-submodules=dirty -- tools/d2probe` не пуст),
  скрипт дописывает к хешу суффикс `+dirty` — журнал честно говорит, что точной
  ревизии для него не существует. `--ignore-submodules=dirty` здесь потому, что
  рабочие деревья `D2RSG`/`SLikeNet` патчатся `apply-patches.sh` и «грязны»
  всегда — это норма, а не незакоммиченный код (см. `NOTICE.md`).

Проверка журнала (JSON в каждой строке, поля `ev`/`seq`/`tid`, `seq` без повторов,
`units`/`order` у снимков боя, конец строки без `\r`):

```
python tools/d2probe/tests/probe_json_check.py <журнал.jsonl>
```

## Установка и снятие журнала

`tools/d2probe/install.py` ставит и снимает собранную `Release/mss32.dll` в каталоге игры.
Каталог по умолчанию — `<D2_GAME_DIR или ../disciples-modding/game-pristine>/../game-dev`
(тот же способ найти игру, что и у `tools/sg_golden.py`); переопределяется флагом `--game`.
Обе изменяющие команды (`install`, `--restore`) отказываются работать, пока запущен
`Discipl2.exe` (`tasklist`), — заменять DLL под работающим процессом бессмысленно и опасно
для файлов рядом. `--status` ничего не меняет и эту проверку не делает.
Установка отказывает и сборке старше исходников: ревизия, которую `build.ps1` вшивает в DLL, должна
содержать последний коммит `tools/d2probe/mss32` и `tools/d2probe/patches` (19.10.2026 из основного каталога
чуть не ушла в игру сборка от 30.09 поверх зонда 7.5). Пересборка — `pwsh -File tools/d2probe/build.ps1`,
обход — `--allow-stale`; `--status` печатает ревизию стоящей DLL.

```
python tools/d2probe/install.py                  # поставить зонд (по умолчанию)
python tools/d2probe/install.py --autobattle      # то же + маркер probe/autobattle
# ВНИМАНИЕ (06.09): под маркером сторона человека не ходит — очередь искажена; для корпуса
# ходов ставить БЕЗ маркера, бой доигрывает кнопка «Быстрый поединок» (corpus_run.py).
python tools/d2probe/install.py --vanilla-globals # то же + эталонные таблицы Globals
python tools/d2probe/install.py --vanilla-globals --coverage # то же + probe/coverage.txt (покрытие кода, схема 7.3)
python tools/d2probe/install.py --restore         # вернуть тулсет 0.14.1 (и Globals, если подменялись)
python tools/d2probe/install.py --status          # что стоит сейчас, ничего не меняя
python tools/d2probe/install.py --game <путь> ... # другой каталог игры
```

`--vanilla-globals` нужен потому, что чистый движок (без хуков тулсета 0.14.1) не умеет читать
модифицированные таблицы `Globals/` — в `game-dev` там остатки мода (`Gattacks`, `Gunits`,
`Gmodif`, `GUmodif.dbf`, `LModifS` и др., которые тулсет читал своими хуками) — и с ними
падает уже при входе в главное меню. Флаг подменяет `<игра>/Globals` на эталон из
`<игра>/../game-pristine/Globals` (`shutil.copytree`), предварительно переименовав исходный
каталог в `Globals.modded`, — это единственное, что установщик трогает кроме `mss32.dll` и
`probe/`. Если `Globals.modded` уже есть (например, подмена сделана руками теми же именами),
второй запуск ничего не переставляет, а сверяет `Gunits.dbf` побайтово с эталоном и сообщает
результат. Отсутствие `GUmodif.dbf` в скопированном эталоне — норма: это таблица тулсета,
её в ванильной игре не бывает. `--restore` возвращает `Globals.modded` обратно в `Globals`
независимо от того, был ли поставлен зонд (бэкап `mss32.dll.v0141` может отсутствовать),
а `--status` печатает состояние строкой `Globals: vanilla (Globals.modded present)` /
`Globals: as-is`. Обе операции подчиняются той же проверке на запущенный `Discipl2.exe`,
что и остальной install/restore.

`install` при первом запуске сохраняет текущий `mss32.dll` как `mss32.dll.v0141` (бэкап
тулсета 0.14.1, каким он был до зонда) — если бэкап уже есть, второй запуск его не трогает,
только переставляет саму DLL. Перед самим бэкапом текущий `mss32.dll` сверяется по размеру и
sha256 с известным тулсетом 0.14.1 (константы `TOOLSET_0141_SIZE`/`TOOLSET_0141_SHA256` в
скрипте); если файл им не соответствует, `install` отказывается — иначе бэкап, снятый уже
после удаления `mss32.dll.v0141` кем-то, при уже стоящем зонде тихо записал бы зонд под именем
0.14.1 и оригинал был бы потерян безвозвратно. `--force-backup` снимает эту сверку, если
пользователь уверен, что текущий `mss32.dll` — действительно 0.14.1 (например, другая сборка).
Дальше копируется `tools/d2probe/mss32/Release/mss32.dll` и создаётся каталог `probe/` (сам по
себе — переключатель режима зонда, см. «Режим зонда» выше). `--restore` копирует
`mss32.dll.v0141` обратно в `mss32.dll` и удаляет маркер `probe/autobattle`; журналы
(`probe/*.jsonl`) не трогает ни `install`, ни `--restore` — это данные, а не состояние
установки. Про сам маркер и что он включает — см. «Автобой» выше.

Журналы одного зонда лежат в `<игра>/probe/<yyyymmdd-hhmmss>-<pid>.jsonl`, по одному на
процесс игры (см. «Режим зонда»). Проверка журнала — две ступени: `probe_json_check.py`
(валидность JSONL, см. выше) и `tools/d2probe/tests/probe_invariants.py <журнал.jsonl>` —
семь проверок формы боя (парность `battle_start`/`battle_end` по `seq` — с равенством счётчиков
как отдельным условием, а не только порядком: обрыв журнала (меньше `battle_end`, чем
`battle_start`) — ровно то, что должно ловиться; `miss.rng_after >= rng_before`; рост HP между
`turn_before`/`turn_after` одного хода — пары ищутся по `seq` (порядок вызовов), а не по
порядку строк файла, где вложенные события пишутся раньше внешних, — как счётчик, лечение
законно; `order` в `round`/`turn_before`/`turn_after`/`battle_units` — без повторов, без
`"INVALID-ID"` и целиком из `units` того же события, плюс счётчик событий с пустым `order`:
если пуст **во всех** событиях, проверка не пройдена (пустое поле ничего не доказывает, и
«нарушений не нашлось» в пустоте — не результат), если в части — предупреждение;
`miss <= power`; и безусловные итоги по `ev`, куда входит и `battle_units`. Код
выхода 1 только на невалидном JSON, отсутствии/неравном числе/неверном порядке
`battle_start`/`battle_end` и на битом или сплошь пустом `order` — остальное печатается как информационный
счётчик, расхождение разбирается по журналу, а не считается провалом. Проверка [2] по-прежнему
смотрит только на пару `battle_start`/`battle_end` — появление `battle_units` её не меняет.
