#!/usr/bin/env python3
"""Маршрут живого прогона Ф6: движение из `scenario_c4.build_route` плюс действия на ходах.

    python tools/f6_route.py --scenario ../disciples-modding/game-dev/scenarios/f6_route.sg \
        --out tools/f6_route.json
    python tools/f6_route.py selftest      # без игры и без карты

Схема `route.json` Ф6 **надстраивает** схему Ф5: поля `turns` (с `turn` и `target`) и
`checkpoints` остаются ровно теми же, и `corpus_c4_run.load_route` читает такой файл без
правок. Ново только два поля:

* у хода — необязательный список `actions`: что сделать, стоя на цели этого хода
  (`"when": "before"` — до шага, иначе после);
* у файла — `expect`: сколько событий корпуса какого типа прогон обязан породить. Скрипт
  прогона печатает «ожидалось / получено» по каждому и этим отвечает на «пустой лог ничего
  не доказывает».

Виды действий (`do`) и то, чем каждое проверяется в журнале:

| `do` | что делает | чем проверяется |
|---|---|---|
| `verify_squad` | быстрое сохранение и сверка состава отряда с картой | сам `.sg`, без журнала |
| `manual` | печатает инструкцию пользователю (мышь там, где хоткея нет) и ждёт | событие `expect_ev` и/или `verify` по сохранению |
| `cast` | `S`, цифра страницы, `index`×`↓`, `Enter`, клик по клетке | казна следующего дня (`income.bank`) |
| `visit` | шаг на клетку объекта | открывшееся окно объекта по кадру (доля чёрного); `site` заход не порождает |
| `attack` | шаг на клетку нейтрала, затем «Быстрый поединок» | `battle_start` и `battle_end` |

**Почему часть шагов ручная.** `docs/game-hotkeys.md` (ранг 1, проверено пользователем
08.09.2026) прямо говорит: выбор здания на схеме столицы и выбор юнита в окне найма — «только
ЛКМ», хоткея нет. Координаты этих кликов из `Interf.dlg` не выводятся (`Gbuild.POS_*` у
Империи все нули, а схема рисуется картинкой), и выдумывать их было бы хуже, чем попросить
пользователя: он на прогоне присутствует. Драйвер печатает, что нажать, а затем **проверяет
результат числом** — постройка обязана появиться в `CPlayerBuildings` сохранения, найм — в
отряде столицы.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import scenario_c4 as sc  # noqa: E402
import make_f6_map as f6  # noqa: E402
import sgfile as sg  # noqa: E402

CHECKPOINT_EVERY = 10

# Заклинания смены террейна (Lspell CATEGORY 11): они переписывают грунт, поэтому стоят
# последними СРЕДИ КАРТОЧНЫХ ходов — см. build_actions. Боевой хвост идёт уже после них, и
# переписанный грунт ему безразличен: там меряется бой, а не туман и не рост земли.
TERRAIN_SPELLS = {"G000SS0107", "G000SS0120"}

# Заклинания, которым нужен ПОТРЁПАННЫЙ отряд: на полном здоровье игра отвечает свитком
# «заклинание не возымеет эффекта», и каст пропадает впустую. Живой прогон 08.09.2026: Исцеление
# на неповреждённый отряд на ходу 2 дало такой свиток, он остался на экране, драйвер стал искать
# под ним карту и провалил следующий шаг. Поэтому они кастуются на ходу ВТОРОГО боя — после боя
# у руины отряд уже ранен, и `HEAL_QTY 30` наконец меряется на недолеченном, как и задумано.
AFTER_BATTLE_SPELLS = {"G000SS0007"}

# Соседство в Disciples 2 — восемь клеток вокруг, поэтому «не сосед» это зазор по Чебышёву >= 2.
# Ни одна клетка карточной части маршрута не смеет подойти к нейтралу ближе: 08.09.2026 такое
# соседство на первом же ходу начало бой и оборвало прогон.
MIN_NEUTRAL_GAP = 2

# Куда целить каст «рядом» (`on == "near"`). Живой прогон 08.09.2026 12:11: все три таких каста
# — призыв, раскрытие тумана, смена террейна — целились в клетку САМОГО ОТРЯДА, и игра каждый
# отменяла (пользователь: «призыв на другую свободную клетку нужно делать, а раскрытие — в туман
# войны»). Третий из них оборвал прогон на ходу 44. Поэтому цель считается здесь, при сборке:
#   * призыв и смена террейна — соседняя (Чебышёв 1) проходимая клетка равнины или леса, не
#     занятая объектом, нейтралом и не лежащая на пути отряда (призванный не должен встать
#     поперёк следующего хода);
#   * раскрытие тумана — клетка на FOG_REACH по оси x от отряда. РАНГ 3: обзор лидера в Ф5 мерился
#     радиусом 4..7, восемь клеток на третьем ходу заведомо ещё в тумане; снимает журнал прогона.
FOG_SPELLS = {"G000SS0009"}
FOG_REACH = 8


_AREA: dict[str, int] = {}


def spell_area(spell: str) -> int:
    """`Gspells.AREA` — сторона квадрата площади (задача 9, ранг 1: размер, не категория)."""
    if not _AREA:
        from dbf_dump import read
        _AREA.update({r["SPELL_ID"].upper(): int(r["AREA"]) for r in read(f6.sq.GLOBALS / "Gspells.dbf")["rows"]})
    return _AREA[spell.upper()]


def near_target(scen: sc.Scenario, hero: tuple[int, int], spell: str,
                avoid: set[tuple[int, int]]) -> tuple[int, int]:
    """Клетка для каста «рядом» с отряда, стоящего на `hero`. Нет подходящей — SystemExit.

    Площадное заклинание целится так, чтобы ОТРЯД ОСТАЛСЯ ВНЕ ПЛОЩАДИ: пользователь 08.09.2026
    — «ты кастуешь заклинание, которое растит лес рядом с персонажем… может сразу будет
    кастовать его на нормальном расстоянии от отряда?». Роща 3×3 и Лес 7×7 (`Gspells.AREA`)
    в соседней клетке накрыли бы и клетку отряда. Дистанция = AREA // 2 + 1, вся площадь
    обязана лежать в карте, центр — равнина (чтобы смена террейна была видна в сохранении).
    """
    size = len(scen.grid)
    inside = lambda x, y: 0 <= x < size and 0 <= y < size  # noqa: E731
    if spell in FOG_SPELLS:
        for dx in (FOG_REACH, -FOG_REACH):
            x, y = hero[0] + dx, hero[1]
            if inside(x, y):
                return x, y
        raise SystemExit("раскрытие тумана: от %s ни в одну сторону по x нет %d клеток" % (hero, FOG_REACH))
    area = spell_area(spell)
    half, dist = area // 2, area // 2 + 1
    if area > 1:
        for dx, dy in ((dist, 0), (-dist, 0), (0, dist), (0, -dist)):
            x, y = hero[0] + dx, hero[1] + dy
            if not (half <= x < size - half and half <= y < size - half) or (x, y) in avoid:
                continue
            if scen.step_cost(x, y) is None or scen.ground(x, y) != sc.PLAINS:
                continue
            return x, y
        raise SystemExit("каст %s (площадь %d×%d): в %d клетках от %s нет равнины с площадью в карте"
                         % (spell, area, area, dist, hero))
    for dx, dy in ((1, 0), (0, 1), (1, 1), (-1, 0), (0, -1), (-1, -1), (1, -1), (-1, 1)):
        x, y = hero[0] + dx, hero[1] + dy
        if not inside(x, y) or (x, y) in avoid:
            continue
        if scen.step_cost(x, y) is None or scen.ground(x, y) not in (sc.PLAINS, sc.FOREST):
            continue
        return x, y
    raise SystemExit("каст %s: вокруг %s нет свободной клетки равнины или леса вне пути" % (spell, hero))

# Путевые точки: сначала вдоль ряда объектов (лавка, наёмники, наставник, руина), потом та же
# змейка по полосам леса и воды, что и в Ф5, — числом дней прогон обязан догнать сохранения
# Ф5 (10/20/30/40), иначе рост земли не с чем сверять.
WAYPOINTS = [
    "14,11", "19,11", "24,11", "29,11", "34,11",       # ряд объектов
    "34,20", "14,20",                                   # к жиле (12,20) — встаём рядом
    "14,26", "34,26",                                   # через полосу воды
    "34,14", "14,14", "14,26", "34,26", "34,14",        # круги через лес и воду
    "14,20",
]

# Заклинания прогона: (id, страница уровня, порядковый номер на странице, куда, зачем).
# `index` — РАНГ 3 (порядок внутри страницы предполагается по возрастанию SPELL_ID).
CASTS = [
    ("G000SS0100", 1, 2, "self", "Скорость: RESTORE_M 30 и модификатор MoveAllowance — "
                                 "единственное заклинание с двумя эффектами сразу, порядок открыт"),
    ("G000SS0017", 4, 0, "self", "Призыв к оружию: модификатор на свой отряд ПРЯМО ПЕРЕД БОЕМ — "
                                 "единственная проверка, что цепочка доезжает до боевых формул"),
    ("G000SS0004", 1, 0, "enemy", "Молния: L_ATTACK на карте (боевых заклинаний в игре нет)"),
    ("G000SS0064", 1, 1, "enemy", "Слабость: L_LOWER, модификатор на чужой отряд"),
    ("G000SS0007", 2, 0, "self", "Исцеление: HEAL_QTY 30 на недолеченном отряде"),
    ("G000SS0008", 2, 1, "near", "Призывание: Живой Доспех — L_SUMMON"),
    ("G000SS0009", 2, 2, "near", "Истинное зрение: L_UNFOG, AREA 5"),
    ("G000SS0107", 3, 0, "near", "Создание Рощи: смена террейна, GROUND_CAT 1, AREA 3 — "
                                 "9 клеток квадратом против 5 ромбом"),
    ("G000SS0120", 5, 0, "near", "Создание Леса: смена террейна, AREA 7 — 49 против 25"),
]


def load(scenario: Path) -> sc.Scenario:
    if not scenario.exists():
        raise SystemExit("нет карты %s — сначала python tools/make_f6_map.py" % scenario)
    return sc.Scenario(scenario)


def movement_route(scen: sc.Scenario) -> dict:
    """Ходы по клеткам: то же, что `scenario_c4.py route`, включая первый ход — выход из столицы."""
    own = scen.own_stacks()
    if not own:
        raise SystemExit("в сценарии нет отряда игрока")
    stack = own[0]
    budget = int(stack["MOVE"]) or sc.DEFAULT_MOVE
    # Клетки нейтралов Дейкстре запрещены. Движок их непроходимыми НЕ считает (класс `KC` не
    # входит в `Movement.BlockingKinds`), и путь через них строится — но шаг на нейтрала это
    # бой, а не шаг: он придёт не на том ходу, `play_turn` не дождётся `stack_move` и прогон
    # оборвётся. Бои в Ф6 — отдельное действие `attack` на заранее названном ходу.
    scen.blocked = scen.blocked | {(int(s["POS_X"]), int(s["POS_Y"]))
                                   for s in scen.neutral_stacks()}
    start = (int(stack["POS_X"]), int(stack["POS_Y"]))
    gate = f6.GATE if stack.get("INSIDE", sg.NULL_ID) != sg.NULL_ID else start
    waypoints = [gate] + [sc.parse_xy(w) for w in WAYPOINTS]
    route = sc.build_route(scen, waypoints, budget, stack["STACK_ID"], CHECKPOINT_EVERY)
    if gate != start:
        route["turns"] = ([{"turn": 1, "target": list(gate), "note": "выход из столицы"}]
                          + [{**t, "turn": t["turn"] + 1} for t in route["turns"]])
    route["checkpoints"] = [n for n in range(CHECKPOINT_EVERY, len(route["turns"]) + 1,
                                             CHECKPOINT_EVERY)]
    return route


def nearest_turn(route: dict, tile: tuple[int, int], after: int = 0,
                 reach: int = 3) -> int:
    """Номер хода, с цели которого до `tile` не дальше `reach` по Чебышёву, начиная с `after`+1.

    Действие привязывается к ходу, а не к клетке: драйвер стоит на цели хода, и всё, до чего
    он оттуда дотянется за один шаг, он сделает в тот же ход.
    """
    best, best_d = None, 1 << 30
    for t in route["turns"]:
        if t["turn"] <= after:
            continue
        d = sc.chebyshev(tuple(t["target"]), tile)
        if d < best_d:
            best, best_d = t["turn"], d
    if best is None or best_d > reach:
        raise SystemExit("до клетки %s ни один ход не подходит ближе %d (лучшее %s)"
                         % (tile, reach, best_d))
    return best


def cells(route: dict) -> list[tuple[int, int]]:
    """Все клетки маршрута: и цели ходов, и клетки пути между ними.

    Проверять только цели мало. Бой начинается от шага на нейтрала, а шаг делает игра по
    своему пути; наш `path` — лучшее, что о нём известно заранее, и соседство считается по
    нему тоже.
    """
    return ([tuple(p) for p in route["path"]]
            + [tuple(t["target"]) for t in route["turns"]])


def gaps_to(route_cells: list[tuple[int, int]],
            tiles) -> dict[tuple[int, int], int]:
    """Минимальное расстояние по Чебышёву от клеток маршрута до каждой из `tiles`."""
    return {t: min(sc.chebyshev(c, t) for c in route_cells) for t in tiles}


def check_gap(route_cells: list[tuple[int, int]], tiles, what: str) -> dict:
    """Напечатать зазор числом и уронить сборку, если хоть один нейтрал оказался соседом."""
    gaps = gaps_to(route_cells, tiles)
    print("  зазор до нейтралов (%s): %s" % (what, ", ".join(
        "(%d,%d)=%d" % (t[0], t[1], d) for t, d in sorted(gaps.items()))))
    bad = {t: d for t, d in gaps.items() if d < MIN_NEUTRAL_GAP}
    if bad:
        raise SystemExit("%s: клетка маршрута вплотную к нейтралу %s — бой начнётся раньше "
                         "времени, как 08.09.2026 на первом же ходу" % (what, bad))
    return gaps


def approach_tile(scen: sc.Scenario, target: tuple[int, int],
                  frm: tuple[int, int]) -> tuple[int, int]:
    """Клетка, с которой ход нападает: не ближе MIN_NEUTRAL_GAP к цели, из равных — ближе к `frm`.

    Встать НА цель боя нельзя: клетка нейтрала запрещена Дейкстре, а руина занимает 3x3 и
    целиком непроходима. Но и вплотную вставать нельзя, причём по другой причине: движение хода
    в драйвере идёт ДО действий этого хода (`corpus_f6_run.play_map`), и бой, начатый шагом,
    придёт раньше кастов — то есть ровно та связка «модификаторы, а сразу за ними бой», ради
    которой фаза и затевалась, распалась бы. Поэтому ход кончается за две клетки, а на цель
    шагает уже действие `attack`, после кастов. Двух клеток отряду хватает: бюджет хода 20
    очков, равнина стоит 2.
    """
    for r in range(MIN_NEUTRAL_GAP, 6):
        ring = [(target[0] + dx, target[1] + dy)
                for dx in range(-r, r + 1) for dy in range(-r, r + 1)
                if max(abs(dx), abs(dy)) == r
                and 0 <= target[0] + dx < scen.size and 0 <= target[1] + dy < scen.size
                and scen.step_cost(target[0] + dx, target[1] + dy) is not None]
        if ring:
            return min(ring, key=lambda c: sc.chebyshev(c, frm))
    raise SystemExit("к клетке %s не подойти: вокруг на пять клеток всё непроходимо" % (target,))


def append_leg(route: dict, scen: sc.Scenario, goal: tuple[int, int]) -> tuple[int, list]:
    """Дописать ходы от конца маршрута до `goal`; последний ход кончается ровно на `goal`.

    После боя отряд в игре стоит НА клетке цели, а модель считает от клетки подхода — расхождение
    в одну-три клетки. Оно безобидно: `play_turn` кликает по цели следующего хода и печатает
    «не дошёл», если бюджета не хватило, но прогон на этом не рвётся.
    """
    frm = tuple(route["turns"][-1]["target"])
    leg = sc.build_route(scen, [frm, goal], route["move_budget"], route["stack"],
                         CHECKPOINT_EVERY)
    if not leg["turns"]:
        raise SystemExit("до %s из %s не вышло ни одного хода" % (goal, frm))
    n = len(route["turns"])
    route["turns"] += [{**t, "turn": t["turn"] + n} for t in leg["turns"]]
    route["path"] += leg["path"][1:]
    route["waypoints"].append(list(goal))
    return len(route["turns"]), cells(leg)


def add(route: dict, turn: int, action: dict) -> None:
    for t in route["turns"]:
        if t["turn"] == turn:
            t.setdefault("actions", []).append(action)
            return
    raise SystemExit("хода %d в маршруте нет" % turn)


def build_actions(route: dict, scen: sc.Scenario) -> dict:
    """Расставить действия по ходам и посчитать, чего ждать в журнале.

    Маршрут делится на две части, и порядок между ними — итог живого прогона 08.09.2026.

    **Сначала карточная часть, все 45 ходов, и в ней ни одного боя.** Прогон 08.09 оборвался на
    первом же ходу: шаг на (10,10) встал вплотную к нейтралу, начался бой, и отряд его проиграл
    — а он собран из поддержки (Revive, DrainLevel, GiveAttack, Doppelganger, TransformSelf),
    урона в нём почти нет, и усилить его нельзя: на этих пяти юнитах держатся пять классов атак
    целиком. После поражения карты на экране нет, и 90 событий `terrain_day`, 90 `income`, три
    `site` и рост земли — то, ради чего карта и нужна, — не сняты. Поэтому бой не смеет стоять
    раньше, чем карта отснята.

    **Потом боевой хвост, слабейший первый: руина, нейтрал A, нейтрал B.** Поражение в любом
    бою обрывает прогон — и это уже нормально, карта к тому моменту в кармане.

    Связка «модификаторы и сразу бой» — главная проверка фазы (боевых заклинаний в игре нет
    вовсе, и это единственный способ увидеть, доезжает ли цепочка модификаторов до боевых
    формул) — переезжает целиком и не разрывается. Стоит она на первом бою ПО ОТРЯДУ, то есть
    на нейтрале A, а не на руине: `L_ATTACK` и `L_LOWER` наводятся на чужой отряд, а у руины
    отряда нет — её охрана лежит внутри записи `CMidRuin` (`make_f6_map.ruin_body`), стеком на
    карте не стоит и целью заклинания быть не может. Руина при этом остаётся первой, как самый
    слабый бой: шесть юнитов первого уровня против пяти-шести третьего-четвёртого у нейтралов.
    """
    neutrals = {(int(s["POS_X"]), int(s["POS_Y"])): s for s in scen.neutral_stacks()}
    if len(neutrals) != 2:
        raise SystemExit("нейтральных отрядов %d, ожидалось 2" % len(neutrals))
    near_gate = min(neutrals, key=lambda p: sc.chebyshev(p, f6.GATE))
    far = next(p for p in neutrals if p != near_gate)

    sites = [(kind, (int(s["POS_X"]) + 1, int(s["POS_Y"]) + 1))
             for kind, s in scen.sites]
    ruins = [(int(r["POS_X"]) + 1, int(r["POS_Y"]) + 1) for r in scen.ruins]
    if len(sites) != 3 or len(ruins) != 1:
        raise SystemExit("объектов: сайтов %d, руин %d — ожидалось 3 и 1" % (len(sites), len(ruins)))

    # Карточная часть не смеет пройти вплотную к нейтралу — иначе бой придёт сам, без действия
    # `attack`, и в чужом порядке. Проверка числом, до всякой игры.
    check_gap(cells(route), neutrals, "карточные ходы")

    # Ход 1 — всё, что делается только изнутри столицы: постройка и найм. Герой ещё внутри,
    # окно армии показывает отряд столицы (docs/game-hotkeys.md, «Найм»).
    #
    # Порядок намеренный. Сначала сверка состава: если старт сцены урезал отряд, пять классов
    # атак не воспроизводятся вовсе, и остальные полтора часа прогона тратить незачем. Потом
    # каждый ручной шаг открывает СВОЁ окно с карты (`A` + буква) и заканчивается возвратом на
    # карту: драйвер между шагами делает быстрое сохранение, а оно работает только с карты.
    #
    # Цель — Библиотека и Маг, а не Белая башня. Прогон грузит патченое сохранение, и там
    # предпосылки, вообще говоря, должны пережить загрузку — но ПРОВЕРЕНО это не было ни разу,
    # а в сценарии они гарантированно стираются (`fidelity-f5.md` §5 п. 8: старт пересоздаёт
    # игрока по лорду; в живой игре 08.09.2026 в окне построек были только здания с
    # `REQUIRED = g000000000`). Библиотека строится независимо от того, сработал патч построек
    # или нет, поэтому шаг не может провалиться по этой причине. Прогону нужны факт постройки
    # и факт найма, а не конкретное здание; сам вопрос «переживает ли правленый
    # CPlayerBuildings загрузку» снимает пробная постройка из `patch_f6_save.PROBE_BUILDING`.
    add(route, 1, {"do": "verify_squad", "when": "before",
                   "text": "состав отряда после старта сцены: пережили ли пять солдат "
                           "лидерство героя 4 (ранг 3)"})
    add(route, 1, {"do": "manual", "when": "before", "keys": ["A", "S"],
                   "text": "Открыто окно построек столицы. ЛКМ по «Библиотеке» (ветка магов, "
                           "первое здание — доступно сразу, 200 золота), затем Enter (купить) "
                           "и Enter (подтвердить) — игра сама вернёт в окно столицы. После этого "
                           "ОДИН Esc — на карту; второй на карте открыл бы опции.",
                   "seconds": 30, "expect_ev": "build_status",
                   "verify": {"kind": "building", "id": f6.TARGET_BUILDING}})
    add(route, 1, {"do": "manual", "when": "before", "keys": ["A", "P"],
                   "text": "Открыто окно армии столицы. ЛКМ по свободному слоту ПРАВОЙ стороны "
                           "(слот 1 занят стражем), выберите «Ученика» курсором (базовый юнит "
                           "ветки магов, 60 золота — Маг в найме НЕ предлагается, здания открывают "
                           "не найм, а улучшение), затем Enter и Enter. После этого Esc, "
                           "Esc — вернуться на карту.",
                   "seconds": 30, "expect_ev": "hire_list",
                   "verify": {"kind": "hire", "id": f6.TARGET_HIRE}})

    # Касты карточной части — по одному на ход, чтобы дневная трата маны читалась по
    # income.bank. Связка «модификаторы + бой» (CASTS[:2] и всё, что на врага) сюда НЕ входит:
    # её место — боевой хвост, см. докстринг.
    #
    # Смена террейна — на последних КАРТОЧНЫХ ходах: она переписывает грунт вокруг отряда, а
    # грунт это стоимость шага, и все последующие карточные ходы разошлись бы со стартовой
    # картой, по которой харнесс С4 считает движение и туман. Боевому хвосту, который идёт
    # дальше, это уже безразлично — там не меряют ни туман, ни рост земли.
    used = 1                                # ход 1 занят ручными шагами, касты идут со второго
    last = len(route["turns"])
    tail = last - len([c for c in CASTS if c[0] in TERRAIN_SPELLS])
    claimed: set[tuple[int, int]] = set()   # площади уже назначенных кастов «рядом»
    for spell, page, index, where, why in CASTS[2:]:
        if where == "enemy":
            continue                       # уедет в боевой хвост, к первому бою по отряду
        if spell in AFTER_BATTLE_SPELLS:
            continue                       # уедет ко второму бою, когда отряд уже потрёпан
        if spell in TERRAIN_SPELLS:
            tail += 1
            turn = tail
        else:
            used = nearest_turn(route, tuple(route["turns"][used]["target"]), after=used,
                                reach=99)
            turn = used
        hero = tuple(route["turns"][turn - 1]["target"])
        if where == "near":
            # `claimed` — площади уже назначенных кастов: Роща (ход 44) и Лес (ход 45) иначе
            # целились в одну клетку (18,20), и ко второму касту её центр был бы уже лесом —
            # смена террейна могла бы отказать, и замер 7×7 пропал бы.
            avoid = set(cells(route)) | claimed | {(int(s["POS_X"]), int(s["POS_Y"]))
                                                   for s in scen.neutral_stacks()}
            target = near_target(scen, hero, spell, avoid)
            half = spell_area(spell) // 2
            claimed |= {(target[0] + dx, target[1] + dy)
                        for dx in range(-half, half + 1) for dy in range(-half, half + 1)}
        else:
            target = hero
        add(route, turn, {"do": "cast", "spell": spell, "page": page, "index": index,
                          "target": list(target), "on": where, "note": why})

    for kind, tile in sites:
        turn = nearest_turn(route, tile)
        add(route, turn, {"do": "visit", "target": list(tile), "kind": kind,
                          "note": "объект карты: подтверждается открывшимся окном; событие site "
                                  "заход НЕ порождает (измерено 08.09.2026)"})

    # -------------------------------------------------------------- боевой хвост
    map_turns = len(route["turns"])
    bundle = CASTS[:2] + [c for c in CASTS if c[3] == "enemy"]
    battles = [
        (ruins[0], "руина",
         "руина с охраной из шести юнитов 1 уровня — самый слабый бой прогона, поэтому первый; "
         "после победы добыча (CASH + ITEM)"),
        (near_gate, "нейтрал A",
         "первый бой ПО ОТРЯДУ: заклинания на себя и на врага наложены прямо перед ним, и "
         "только здесь видно, доезжает ли цепочка модификаторов до боевых формул"),
        (far, "нейтрал B", "второй нейтральный отряд — последнее, чем прогон может закончиться"),
    ]
    battle_turns = {}
    fought: set[tuple[int, int]] = set()
    prev = map_turns
    for tile, kind, why in battles:
        turn, leg = append_leg(route, scen, approach_tile(
            scen, tile, tuple(route["turns"][-1]["target"])))
        # Подход к своей цели не смеет задеть ЧУЖОГО нейтрала: тот начал бы бой не в свой черёд.
        # Уже отвоёванных в проверке нет — их на карте больше не будет.
        rest = [n for n in neutrals if n != tile and n not in fought]
        if rest:
            check_gap(leg, rest, "подход к %s, ходы %d..%d" % (kind, prev + 1, turn))
        if tile == near_gate:
            for spell, page, index, where, spell_why in bundle:
                add(route, turn, {"do": "cast", "spell": spell, "page": page, "index": index,
                                  "target": list(route["turns"][turn - 1]["target"])
                                            if where == "self" else list(near_gate),
                                  "on": where, "note": spell_why})
        add(route, turn, {"do": "attack", "target": list(tile), "kind": kind, "note": why})
        if kind == "руина":
            # Исцеление — СРАЗУ после боя, на том же ходу. Пользователь 08.09.2026: за три хода
            # ожидания отряд восстановился сам (регенерация каждый ход), и каст на 53-м лечил
            # уже здоровых. Цель "stack" — драйвер берёт клетку отряда из последнего stack_move
            # журнала: после боя отряд стоит не на цели хода, а там, куда шагнул к руине.
            for spell, page, index, where, spell_why in CASTS:
                if spell in AFTER_BATTLE_SPELLS:
                    add(route, turn, {"do": "cast", "spell": spell, "page": page, "index": index,
                                      "target": "stack", "on": where,
                                      "note": spell_why + " (сразу после боя у руины)"})
        battle_turns[kind] = turn
        fought.add(tile)
        prev = turn

    # Итоговый инвариант всего маршрута: ни один ход НИГДЕ не кончается по соседству с
    # нейтралом. Ходы карточной части — чтобы бой не пришёл раньше срока, ходы боевого хвоста —
    # чтобы бой не пришёл раньше кастов. На нейтрала шагает только действие `attack`.
    check_gap([tuple(t["target"]) for t in route["turns"]], neutrals, "цели всех ходов")

    turns = len(route["turns"])
    route["checkpoints"] = [n for n in range(CHECKPOINT_EVERY, turns + 1, CHECKPOINT_EVERY)]
    route["expect"] = {
        # terrain_day пишется на каждый вызов income, то есть раз в день на игрока (их два).
        "terrain_day": 2 * turns,
        "income": 2 * turns,
        # `site` из обязательных убран 08.09.2026: заход в лавку его НЕ порождает (окно
        # открылось, журнал пуст). Хук updateEncLayoutSite пишет, на что игрок смотрит в
        # энциклопедии, а не куда зашёл. Заход проверяется открывшимся окном (corpus_f6_run.do_visit),
        # а `site`, если придёт, покажется среди «прочих событий».
        "build_status": 1,
        "hire_list": 1,
        "battle_start": 3,
        "battle_end": 3,
        "attack_new": 1,
        "attack_get": 1,
        "immune": 1,
        "xp": 1,
    }
    route["f6"] = {
        "spells": [{"spell": s, "page": p, "index": i, "on": w, "why": y}
                   for s, p, i, w, y in CASTS],
        "target_building": f6.TARGET_BUILDING,
        "target_hire": f6.TARGET_HIRE,
        "squad": f6.SQUAD,
        "index_rank": "3 — порядок заклинаний внутри страницы уровня предполагается по "
                      "возрастанию SPELL_ID; снимает первый же каст прогона",
        "map_turns": map_turns,
        "battle_turns": battle_turns,
        "neutrals": {"A": list(near_gate), "B": list(far)},
        "gap_min": MIN_NEUTRAL_GAP,
    }
    return route


def write_route(scenario: Path, out: Path) -> dict:
    scen = load(scenario)
    route = build_actions(movement_route(scen), scen)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(route, ensure_ascii=False, indent=2), encoding="utf-8")
    print("маршрут %s: ходов %d, контрольные точки %s, действий %d"
          % (out, len(route["turns"]), route["checkpoints"],
             sum(len(t.get("actions", [])) for t in route["turns"])))
    for t in route["turns"]:
        if not t.get("actions"):
            continue
        x, y = t["target"]
        print("  ход %2d -> (%2d,%2d): %s" % (t["turn"], x, y,
              ", ".join(a["do"] + (":" + a.get("spell", a.get("kind", "")) if
                                   a.get("spell") or a.get("kind") else "")
                        for a in t["actions"])))
    f = route["f6"]
    print("  карточных ходов %d (1..%d), боевых %d: %s"
          % (f["map_turns"], f["map_turns"], len(f["battle_turns"]),
             ", ".join("%s на ходу %d" % (k, v) for k, v in f["battle_turns"].items())))
    print("  нейтралы: A %s, B %s" % (tuple(f["neutrals"]["A"]), tuple(f["neutrals"]["B"])))
    print("  ожидается событий: " + " ".join("%s=%d" % kv for kv in sorted(route["expect"].items())))
    return route


def selftest() -> int:
    """Без игры и без карты: привязка действий к ходам и форма файла."""
    route = {"turns": [{"turn": n, "target": [n, 0]} for n in range(1, 11)], "checkpoints": []}
    assert nearest_turn(route, (5, 0)) == 5
    assert nearest_turn(route, (5, 0), after=6) == 7          # after сдвигает вперёд
    add(route, 3, {"do": "visit"})
    assert route["turns"][2]["actions"] == [{"do": "visit"}]
    try:
        nearest_turn(route, (99, 99))
    except SystemExit:
        pass
    else:
        raise AssertionError("недостижимая клетка обязана уронить nearest_turn")
    # Зазор до нейтралов: считается и по целям ходов, и по клеткам пути, и соседство роняет.
    near = {"turns": [{"turn": 1, "target": [10, 10]}], "path": [[10, 10], [11, 10]]}
    assert gaps_to(cells(near), [(12, 9)]) == {(12, 9): 1}
    assert gaps_to(cells(near), [(12, 7)]) == {(12, 7): 3}
    try:
        check_gap(cells(near), [(11, 9)], "проверка")
    except SystemExit:
        pass
    else:
        raise AssertionError("сосед нейтрала обязан уронить сборку маршрута")
    check_gap(cells(near), [(12, 7)], "проверка")

    pages = {}
    for spell, page, index, _, _ in CASTS:
        pages.setdefault(page, []).append((index, spell))
    for page, items in pages.items():
        idx = sorted(i for i, _ in items)
        assert idx == list(range(len(items))), (page, items)
        assert [s for _, s in sorted(items)] == sorted(s for _, s in items), \
            f"страница {page}: index не совпадает с порядком по SPELL_ID"
    assert {s for s, *_ in CASTS} <= set(f6.KNOWN_SPELLS), "кастуем то, чего игрок не знает"
    print("selftest: привязка действий, страницы заклинаний и известные заклинания сошлись")
    return 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("cmd", nargs="?", default="route", choices=["route", "selftest"])
    p.add_argument("--scenario", type=Path, default=f6.OUT)
    p.add_argument("--out", type=Path, default=f6.ROUTE_OUT)
    a = p.parse_args(argv)
    if a.cmd == "selftest":
        return selftest()
    write_route(a.scenario, a.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
