#!/usr/bin/env python3
"""Проверка карты С4 и построение маршрута по ней. Игру не запускает, карту не правит.

    python tools/scenario_c4.py check --scenario ../disciples-modding/game-dev/scenarios/c4_route.sg
    python tools/scenario_c4.py check --scenario ...c4_route.sg --route route.json --minimal
    python tools/scenario_c4.py route --scenario ...c4_route.sg --waypoints 12,14 18,14 18,22 \
        --out route.json
    python tools/scenario_c4.py selftest

check печатает числами всё, что требует таблица шага 1 задачи 2 плана Ф5: размер карты,
гистограмму грунтов, игроков (не-нейтральный должен быть ровно один), объекты по классам с
координатами и грунтом под ними, стартовую казну, длины сплошных участков равнины/леса/воды,
деревню и её расстояние до столицы, руину без охраны, три лавки, кристалл, мешок, нейтральные
отряды и события сценария с категориями условий. Каждое требование — строка «ДА/НЕТ» с числом,
итог — «карта годна» и код возврата 0. С --route дополнительно проверяются требования, которые
без маршрута бессмысленны: нейтралы дальше 3 клеток от маршрута, горы и вода вплотную к нему.
С --minimal проверяется только то, без чего С4 не даст ни одного числа, а деревня, руина,
лавка, наёмники, наставник, мешок и события печатаются строкой «отложено» (см. класс Report).

route строит путь Дейкстрой по проходимым клеткам через путевые точки и режет его на ходы по
бюджету очков движения. Стоимость шага — равнина 2, лес 4, вода 6, РАНГ 1: прочитано в
дизассемблере Discipl2.exe (Gog, 4 474 880), computeMovementCost 0x6027f3, ветки
0x602967-0x6029eb; те же числа стоят в src/D2.Rules/Map/Movement.cs (MovementRules.Vanilla).
Здесь взят простой случай — живой лидер, без дорог и бонусов рас; дорога (1), бонусы (2) и
мёртвый лидер (4/8/12) маршрут не учитывает. Поэтому маршрут — список намерений, а не
утверждение о стоимости: если реальный бюджет разойдётся, ход кончится раньше цели, и это
поймает харнесс по журналу (move_cost, stack_move), а не этот файл.

Разбор .sg — поверх tools/sgfile.py. Раскладки объектов сверены с tools/ksy/sg.ksy (классы
mid_village, mid_ruin, site, mid_crystal, mid_bag, mid_event) и проверены тем, что курсор
дочитывает тело записи ровно до конца на живых картах game-dev/Exports-archive.
"""
from __future__ import annotations

import argparse
import heapq
import json
import struct
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402

GROUND = {0: "равнина", 1: "лес", 3: "вода", 4: "горы"}
PLAINS, FOREST, WATER, MOUNTAIN = 0, 1, 3, 4
# Стоимость шага — ранг 1 (computeMovementCost 0x6027f3, живой лидер, без дороги и бонусов),
# см. шапку. Непроходимые грунты (горы) в словарь не входят.
STEP_COST = {PLAINS: 2, FOREST: 4, WATER: 6}

RACE_NEUTRAL = "G000RR0004"
RACE_EMPIRE = "G000RR0000"
LORD_WARRIOR_EMPIRE = "G000LR0001"   # Glord.dbf: g000LR0001 = Human Warrior Lord, «Полководец»
MAP_SIZE = 48
DEFAULT_MOVE = 20                    # герой g000uu0019: MOVE 20 (Gunits.dbf)

# Раскладки тел записей (порядок полей — из tools/ksy/sg.ksy, маркеры сверяет Cursor._marker).
L_CITY = sg.LAYOUT_CAPITAL           # CITY_ID..POS_Y — общая голова capital и mid_village
L_GROUP = ("GROUP_ID:s " + " ".join(f"UNIT_{k}:s" for k in range(6))
           + " " + " ".join(f"POS_{k}:i" for k in range(6)))
L_VILLAGE_TAIL = ("AIPRIORITY:i PROTECT_B:s REGEN_B:i MORALE:i GROWTH_T:i SIZE:i "
                  "P_O_UN:b P_O_HE:b P_O_HU:b P_O_DW:b P_O_EL:b RIOT_T:i")
L_RUIN = "RUIN_ID:s TITLE:s DESC:s IMAGE:i POS_X:i POS_Y:i CASH:s ITEM:s LOOTER:s AIPRIORITY:i"
L_SITE = "SITE_ID:s IMG_ISO:i IMG_INTF:s TXT_TITLE:s TXT_DESC:s POS_X:i POS_Y:i VISITER:s AIPRIORITY:i"
L_CRYSTAL = "CRYSTAL_ID:s RESOURCE:i POS_X:i POS_Y:i AIPRIORITY:i"
L_BAG = "BAG_ID:s POS_X:i POS_Y:i IMAGE:i AIPRIORITY:i"
L_EVENT_HEAD = ("ID:s NAME_TXT:s HUMAN:b DWARF:b UNDEAD:b HERETIC:b NEUTRAL:b ELF:b "
                "VERHUMAN:b VERDWARF:b VERUNDEAD:b VERHERETIC:b VERNEUTRAL:b VERELF:b "
                "ENABLED:b OCCUR_ONCE:b CHANCE:i ORDER:i COND_QTY:i")
COND_ENTER_ZONE, COND_ENTER_CITY = 2, 3   # sg.ksy event_condition: switch-on category.value

SITE_KINDS = {"MidSiteMerchant": "лавка", "MidSiteMercs": "наёмники",
              "MidSiteTrainer": "наставник", "MidSiteMage": "маг"}

# Классы объектов плана, мимо которых движок не пропускает отряд до всякого грунта. Ранг 1 —
# массив interactiveObjectTypes (isPositionContainsObjects, Gog 0x60285d); те же шесть букв
# стоят в src/D2.Rules/Map/Movement.cs (Movement.BlockingKinds). Маршрут обязан их обходить:
# иначе первый же ход в игре упрётся в собственную столицу и прогон разойдётся с моделью.
BLOCKING_KINDS = frozenset({"FT", "MM", "SI", "RU", "RD", "CR"})


# --------------------------------------------------------------- разбор объектов

def ground_at(grid: list[list[int]], x: int, y: int) -> int:
    return grid[y][x] >> 3


def parse_city(body: bytes) -> dict:
    """Голова capital/mid_village: поля города плюс отряд-гарнизон."""
    c = sg.Cursor(body)
    out = c.fields(L_CITY)
    out.update(c.fields(L_GROUP))
    return out


def parse_village(body: bytes) -> dict:
    c = sg.Cursor(body)
    out = c.fields(L_CITY)
    out.update(c.fields(L_GROUP))
    out["ITEMS"] = [c.str("ITEM_ID") for _ in range(c.count())]
    out.update(c.fields(L_VILLAGE_TAIL))
    return out


def parse_ruin(body: bytes) -> dict:
    c = sg.Cursor(body)
    out = c.fields(L_RUIN)
    out["VISITORS"] = c.count()
    if out["VISITORS"] == 0:          # посетителей нет — дальше сразу отряд-охрана
        out.update(c.fields(L_GROUP))
    return out


def units_of(rec: dict) -> list[str]:
    return [rec[f"UNIT_{k}"] for k in range(6)
            if rec.get(f"UNIT_{k}", sg.NULL_ID) != sg.NULL_ID]


def parse_event(body: bytes) -> dict:
    """Голова события плюс категории условий и эффектов.

    Тела условий и эффектов разбирать незачем: их два десятка видов. Категории читаются
    сканированием маркера CATEGORY, а сканирование проверяется само собой — число найденных
    маркеров обязано равняться COND_QTY + EFFECT_QTY, иначе поле OK=False и утверждать по
    такому событию нечего.
    """
    c = sg.Cursor(body)
    out = c.fields(L_EVENT_HEAD)
    cats: list[int] = []
    i = c.pos
    while True:
        i = body.find(b"CATEGORY", i)
        if i < 0:
            break
        cats.append(struct.unpack_from("<i", body, i + 8)[0])
        i += 8
    j = body.find(b"EFFECT_QTY")
    effects = struct.unpack_from("<i", body, j + 10)[0] if j >= 0 else -1
    nc = int(out["COND_QTY"])
    out["EFFECT_QTY"] = effects
    out["OK"] = effects >= 0 and len(cats) == nc + effects
    out["CONDITIONS"] = cats[:nc] if out["OK"] else []
    out["EFFECTS"] = cats[nc:] if out["OK"] else []
    return out


class Scenario:
    """Всё, что нужно проверке и маршруту, одним разбором файла."""

    def __init__(self, path: Path) -> None:
        self.path = path
        self.sg = sg.Sg.read(path)
        self.grid = sg.tile_grid(self.sg)
        self.size = len(self.grid)
        self.name = sg.scenario_name(self.sg)
        self.players = [sg.fields(r.body, sg.LAYOUT_PLAYER) for r in self.sg.find(sg.WHAT_PLAYER)]
        self.neutral_ids = {p["PLAYER_ID"] for p in self.players if p["RACE_ID"] == RACE_NEUTRAL}
        self.playable = [p for p in self.players if p["RACE_ID"] != RACE_NEUTRAL]
        self.capitals = [parse_city(r.body) for r in self.sg.find(sg.WHAT_CAPITAL)]
        self.villages = [parse_village(r.body) for r in self.sg.find("MidVillage")]
        self.ruins = [parse_ruin(r.body) for r in self.sg.find("MidRuin")]
        self.sites = [(SITE_KINDS.get(r.what.split("AVC")[-1].rstrip("@"), r.what),
                       sg.fields(r.body, L_SITE)) for r in self.sg.records if r.cls == "SI"]
        self.crystals = [sg.fields(r.body, L_CRYSTAL) for r in self.sg.find("MidCrystal")]
        self.bags = [sg.fields(r.body, L_BAG) for r in self.sg.find("MidBag")]
        self.stacks = [sg.parse_stack(r.body) for r in self.sg.find(sg.WHAT_STACK)]
        self.events = [parse_event(r.body) for r in self.sg.records if r.cls == "EV"]
        _, plan = sg.parse_plan(self.sg.one(sg.WHAT_PLAN).body)
        self.blocked = {(x, y) for x, y, e in plan if e[4:6] in BLOCKING_KINDS}

    def ground(self, x: int, y: int) -> int:
        return ground_at(self.grid, x, y)

    def step_cost(self, x: int, y: int) -> int | None:
        """Стоимость входа в клетку или None, если входить нельзя (грунт или объект)."""
        if (x, y) in self.blocked:
            return None
        return STEP_COST.get(self.ground(x, y))

    def neutral_stacks(self) -> list[dict]:
        return [s for s in self.stacks if s["OWNER"] in self.neutral_ids]

    def own_stacks(self) -> list[dict]:
        return [s for s in self.stacks if s["OWNER"] not in self.neutral_ids]

    def ground_hist(self) -> Counter:
        return Counter(t >> 3 for row in self.grid for t in row)

    def longest_run(self, ground: int) -> int:
        """Самый длинный сплошной участок грунта по строке или столбцу."""
        best = 0
        lines = [row for row in self.grid] + [list(col) for col in zip(*self.grid)]
        for line in lines:
            run = 0
            for tile in line:
                run = run + 1 if (tile >> 3) == ground else 0
                best = max(best, run)
        return best


def chebyshev(a: tuple[int, int], b: tuple[int, int]) -> int:
    return max(abs(a[0] - b[0]), abs(a[1] - b[1]))


# --------------------------------------------------------------------- маршрут

def dijkstra(scen: Scenario, start: tuple[int, int], goal: tuple[int, int]) -> list[tuple[int, int]]:
    """Дешевейший путь по проходимым клеткам (8 направлений, как ходит отряд в игре)."""
    size = scen.size
    dist = {start: 0}
    prev: dict[tuple[int, int], tuple[int, int]] = {}
    queue = [(0, start)]
    while queue:
        cost, node = heapq.heappop(queue)
        if node == goal:
            break
        if cost > dist.get(node, 1 << 30):
            continue
        x, y = node
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                if dx == dy == 0:
                    continue
                nx, ny = x + dx, y + dy
                if not (0 <= nx < size and 0 <= ny < size):
                    continue
                step = scen.step_cost(nx, ny)
                if step is None:
                    continue
                nxt, ncost = (nx, ny), cost + step
                if ncost < dist.get(nxt, 1 << 30):
                    dist[nxt], prev[nxt] = ncost, node
                    heapq.heappush(queue, (ncost, nxt))
    if goal not in dist:
        raise SystemExit("нет проходимого пути %s -> %s: проверьте путевые точки" % (start, goal))
    path = [goal]
    while path[-1] != start:
        path.append(prev[path[-1]])
    return list(reversed(path))


# Цель хода обязана быть ВИДНА в начале хода: клетку в тумане войны движок не обсчитывает и
# путь в неё не строит (пользователь, живой прогон 07.09.2026 — ход 2 падал на цели за
# обзором). Обзор героя SCOUT = 5 (круг, Fog.cs); берём с запасом 4 клетки по Чебышёву.
REACH = 4
CAPITAL_SIZE = 5                     # столица занимает 5x5; ворота — на диагонали сразу за ней


def split_turns(scen: Scenario, path: list[tuple[int, int]], budget: int,
                reach: int = REACH) -> list[tuple[int, int]]:
    """Цели ходов: идём по пути, копим стоимость, режем на бюджете очков движения или когда
    клетка уходит дальше reach от начала хода (туман войны)."""
    targets: list[tuple[int, int]] = []
    spent, last, start = 0, path[0], path[0]
    seen = {path[0]}            # клетки текущего хода
    for tile in path[1:]:
        step = STEP_COST[scen.ground(*tile)]
        far = max(abs(tile[0] - start[0]), abs(tile[1] - start[1])) > reach
        # Ход — один клик по цели, игра сама идёт кратчайшим путём. Поэтому внутри хода путь
        # не может возвращаться на уже пройденную клетку (туда-обратно к кромке гор давал цель,
        # равную началу хода: клик по себе — и ни одного stack_move, живой прогон 07.09, ход 11).
        revisit = tile in seen
        if spent + step > budget or far or revisit:
            if last != start:
                targets.append(last)
            spent, start, seen = step, last, {last, tile}
        else:
            spent += step
            seen.add(tile)
        last = tile
    if last != path[0] and (not targets or targets[-1] != last):
        targets.append(last)
    return targets


def build_route(scen: Scenario, waypoints: list[tuple[int, int]], budget: int,
                stack_id: str, every: int) -> dict:
    path: list[tuple[int, int]] = [waypoints[0]]
    for a, b in zip(waypoints, waypoints[1:]):
        path += dijkstra(scen, a, b)[1:]
    targets = split_turns(scen, path, budget)
    turns = [{"turn": n, "target": [x, y]} for n, (x, y) in enumerate(targets, 1)]
    return {
        "scenario": scen.path.stem,
        "stack": stack_id,
        "move_budget": budget,
        "cost_model": {"note": "ранг 1, computeMovementCost 0x6027f3 (Gog): живой лидер, "
                               "без дороги и бонусов рас",
                       "ground": {str(k): v for k, v in STEP_COST.items()}},
        "waypoints": [list(w) for w in waypoints],
        "path": [list(t) for t in path],
        "turns": turns,
        "checkpoints": [n for n in range(every, len(turns) + 1, every)],
    }


# ----------------------------------------------------------------------- check

class Report:
    """Требования делятся на две группы.

    **Минимально достаточные** (`minimal=True`) — то, без чего С4 не даст ни одного числа:
    размер карты, ровно один играбельный игрок с нужным лордом, столица на равнине, три грунта
    с разной ценой шага и непроходимые горы, источник дохода помимо столицы, свой отряд и
    отсутствие боёв на маршруте. С4 меряет казну, позиции, опыт, туман и состав — все пять
    величин снимаются уже на такой карте.

    **Отложенные** (`minimal=False`) — деревня, руина, лавка, наёмники, наставник, мешок и два
    события сценария. Они расширяют покрытие (посещения, найм, обучение, эффекты событий), но
    ни одной из пяти величин критерия не создают, а их запись в `.sg` требует ещё нескольких
    классов записей. Печатаются как «отложено», а не как провал.
    """

    def __init__(self, minimal_only: bool = False) -> None:
        self.rows: list[tuple[bool, str, str, bool]] = []
        self.minimal_only = minimal_only

    def need(self, ok: bool, what: str, got: str, minimal: bool = True) -> None:
        self.rows.append((bool(ok), what, got, minimal))

    def print(self) -> bool:
        checked = [r for r in self.rows if r[3] or not self.minimal_only]
        skipped = [r for r in self.rows if not (r[3] or not self.minimal_only)]
        print()
        print("%-3s %-46s %s" % ("", "требование", "измерено"))
        for ok, what, got, _ in checked:
            print("%-3s %-46s %s" % ("ДА" if ok else "НЕТ", what, got))
        for _, what, got, _ in skipped:
            print("%-3s %-46s %s" % ("--", what, "отложено, не проверяется в этом прогоне"))
        bad = [w for ok, w, _, _ in checked if not ok]
        print()
        if bad:
            print("итог: ПРОВАЛ, не выполнено %d из %d: %s" % (len(bad), len(checked),
                                                               "; ".join(bad)))
            return False
        print("итог: карта годна (%d требований из %d%s)"
              % (len(checked), len(checked),
                 ", отложено %d" % len(skipped) if skipped else ""))
        return True


def check(path: Path, route_path: Path | None, minimal: bool = False) -> int:
    scen = Scenario(path)
    r = Report(minimal_only=minimal)

    print("карта %s, размер %dx%d, имя %r" % (path.name, scen.size, scen.size, scen.name))
    hist = scen.ground_hist()
    for g, n in sorted(hist.items()):
        print("  грунт %d %-9s клеток %5d, длиннейший участок %d"
              % (g, GROUND.get(g, "?"), n, scen.longest_run(g)))

    print("игроков %d, не-нейтральных %d" % (len(scen.players), len(scen.playable)))
    for p in scen.players:
        print("  %s раса=%s лорд=%s казна=%s %r"
              % (p["PLAYER_ID"], p["RACE_ID"], p["LORD_ID"], p["BANK"], p["NAME_TXT"]))

    caps = [(int(c["POS_X"]), int(c["POS_Y"])) for c in scen.capitals]
    for c, xy in zip(scen.capitals, caps):
        print("столица %r (%d,%d) грунт %s владелец %s"
              % (c["NAME_TXT"], xy[0], xy[1], GROUND.get(scen.ground(*xy), "?"), c["OWNER"]))
    for v in scen.villages:
        xy = (int(v["POS_X"]), int(v["POS_Y"]))
        d = min((chebyshev(xy, c) for c in caps), default=-1)
        print("деревня %r (%d,%d) уровень %s владелец %s грунт %s, до столицы %d"
              % (v["NAME_TXT"], xy[0], xy[1], v["SIZE"], v["OWNER"],
                 GROUND.get(scen.ground(*xy), "?"), d))
    for ru in scen.ruins:
        xy = (int(ru["POS_X"]), int(ru["POS_Y"]))
        print("руина %s (%d,%d) cash=%s предмет=%s охрана=%s"
              % (ru["RUIN_ID"], xy[0], xy[1], ru["CASH"], ru["ITEM"], units_of(ru) or "нет"))
    for kind, s in scen.sites:
        print("объект %-10s (%3d,%3d) %r" % (kind, s["POS_X"], s["POS_Y"], s["TXT_TITLE"]))
    for cr in scen.crystals:
        print("кристалл (%3d,%3d) ресурс %s" % (cr["POS_X"], cr["POS_Y"], cr["RESOURCE"]))
    for b in scen.bags:
        print("мешок    (%3d,%3d)" % (b["POS_X"], b["POS_Y"]))
    for s in scen.own_stacks():
        print("свой отряд %s (%3d,%3d) лидер %s MOVE %s"
              % (s["STACK_ID"], s["POS_X"], s["POS_Y"], s["LEADER_ID"], s["MOVE"]))
    for s in scen.neutral_stacks():
        print("нейтрал   %s (%3d,%3d) юнитов %d" % (s["STACK_ID"], s["POS_X"], s["POS_Y"],
                                                    len(units_of(s))))
    for e in scen.events:
        print("событие %s условия=%s эффекты=%s разбор=%s"
              % (e["ID"], e["CONDITIONS"], e["EFFECTS"], "ок" if e["OK"] else "СБИТ"))

    # --- требования таблицы шага 1 задачи 2 плана Ф5
    r.need(scen.size == MAP_SIZE, "размер карты 48x48", "%dx%d" % (scen.size, scen.size))
    r.need(len(scen.playable) == 1, "ровно один не-нейтральный игрок",
           "%d" % len(scen.playable))
    races = [p["RACE_ID"] for p in scen.playable]
    lords = [p["LORD_ID"] for p in scen.playable]
    r.need(races == [RACE_EMPIRE], "раса играбельного — Империя %s" % RACE_EMPIRE, str(races))
    r.need(lords == [LORD_WARRIOR_EMPIRE], "лорд по умолчанию — Полководец %s"
           % LORD_WARRIOR_EMPIRE, str(lords))
    r.need(len(scen.capitals) == 1, "ровно одна столица", "%d" % len(scen.capitals))
    r.need(bool(caps) and all(scen.ground(*c) == PLAINS for c in caps),
           "столица на равнине", ",".join(GROUND.get(scen.ground(*c), "?") for c in caps) or "-")
    r.need(scen.longest_run(PLAINS) >= 10, "участок равнины >= 10 клеток подряд",
           "%d" % scen.longest_run(PLAINS))
    r.need(scen.longest_run(FOREST) >= 6, "участок леса >= 6 клеток подряд",
           "%d" % scen.longest_run(FOREST))
    r.need(scen.longest_run(WATER) >= 4, "участок воды >= 4 клетки подряд",
           "%d" % scen.longest_run(WATER))
    r.need(hist.get(MOUNTAIN, 0) > 0, "на карте есть горы", "%d клеток" % hist.get(MOUNTAIN, 0))

    lvl1 = [v for v in scen.villages
            if int(v["SIZE"]) == 1 and v["OWNER"] in scen.neutral_ids
            and min((chebyshev((int(v["POS_X"]), int(v["POS_Y"])), c) for c in caps),
                    default=99) <= 6]
    r.need(len(lvl1) >= 1, "ничья деревня 1 уровня в 6 клетках от столицы",
           "%d из %d деревень" % (len(lvl1), len(scen.villages)), minimal=False)
    free_ruins = [ru for ru in scen.ruins if not units_of(ru)]
    r.need(len(free_ruins) >= 1, "руина без охраны",
           "%d из %d руин" % (len(free_ruins), len(scen.ruins)), minimal=False)
    kinds = Counter(k for k, _ in scen.sites)
    for kind in ("лавка", "наёмники", "наставник"):
        r.need(kinds.get(kind, 0) >= 1, "объект: %s" % kind, "%d" % kinds.get(kind, 0),
               minimal=False)
    r.need(len(scen.crystals) >= 1, "кристалл маны (доход помимо столицы)",
           "%d" % len(scen.crystals))
    r.need(len(scen.bags) >= 1, "мешок с предметом", "%d" % len(scen.bags), minimal=False)
    r.need(len(scen.own_stacks()) >= 1, "свой отряд на карте", "%d" % len(scen.own_stacks()))
    neutrals = scen.neutral_stacks()
    r.need(len(neutrals) <= 3, "нейтральных отрядов не больше 3", "%d" % len(neutrals))
    r.need(len(scen.events) == 2, "событий сценария ровно 2", "%d" % len(scen.events),
           minimal=False)
    conds = {c for e in scen.events for c in e["CONDITIONS"]}
    r.need({COND_ENTER_ZONE, COND_ENTER_CITY} <= conds,
           "условия событий: вход в область (2) и в город (3)", str(sorted(conds)),
           minimal=False)
    r.need(all(e["OK"] for e in scen.events), "разбор событий сошёлся (CATEGORY = усл.+эфф.)",
           "%d из %d" % (sum(1 for e in scen.events if e["OK"]), len(scen.events)),
           minimal=False)

    if route_path is None:
        print("\n(--route не задан: проверки «нейтралы вне маршрута», «горы и вода вплотную "
              "к маршруту» пропущены — их без маршрута не сформулировать)")
    else:
        route = json.loads(route_path.read_text(encoding="utf-8"))
        tiles = [tuple(t) for t in route["path"]]
        # старт маршрута — клетка, где отряд уже стоит (у нас — своя столица); движок разрешает
        # уйти с занятого объекта, но не войти в него, поэтому первая клетка из проверки исключена
        bad = [t for t in tiles[1:] if scen.step_cost(*t) is None]
        r.need(not bad, "все клетки маршрута проходимы (грунт и объекты)",
               "непроходимых %d%s" % (len(bad), (", первые %s" % bad[:3]) if bad else ""))
        dmin = min((chebyshev((int(s["POS_X"]), int(s["POS_Y"])), t)
                    for s in neutrals for t in tiles), default=99)
        r.need(dmin >= 3, "нейтралы не ближе 3 клеток к маршруту",
               "минимум %d" % dmin if neutrals else "нейтралов нет")
        near = {scen.ground(x + dx, y + dy)
                for (x, y) in tiles for dx in (-1, 0, 1) for dy in (-1, 0, 1)
                if 0 <= x + dx < scen.size and 0 <= y + dy < scen.size}
        r.need(MOUNTAIN in near, "горы вплотную к маршруту", "грунты у маршрута %s"
               % sorted(near))
        r.need(WATER in near, "вода вплотную к маршруту", "грунты у маршрута %s" % sorted(near))
        print("\nмаршрут %s: клеток %d, ходов %d, контрольных точек %s"
              % (route_path.name, len(tiles), len(route["turns"]), route["checkpoints"]))

    return 0 if r.print() else 1


# --------------------------------------------------------------------- selftest

def selftest() -> int:
    """Проверка без игры и без карты С4: маршрут по синтетической сетке.

    Настоящая карта появится только после ScenEdit, а разбор .sg проверяется на живых картах
    game-dev/Exports-archive (курсор дочитывает тело до конца). Здесь проверяется то, что от
    карты не зависит: Дейкстра обходит непроходимое, а нарезка на ходы не превышает бюджет.
    """
    class Fake(Scenario):
        def __init__(self, grid: list[list[int]]) -> None:  # noqa: D107 - тест-двойник
            self.grid = grid
            self.size = len(grid)
            self.path = Path("fake")
            self.blocked = {(9, 4)}       # объект рядом с целью: маршрут обязан его обойти

    size = 12
    grid = [[PLAINS << 3] * size for _ in range(size)]
    for y in range(1, size - 1):
        grid[y][5] = MOUNTAIN << 3        # стена с проходом в строке 0 и size-1
    grid[3][7] = FOREST << 3
    scen = Fake(grid)

    path = dijkstra(scen, (1, 3), (9, 3))
    assert path[0] == (1, 3) and path[-1] == (9, 3), path
    assert all(scen.ground(*t) != MOUNTAIN for t in path), "путь прошёл сквозь горы"
    assert all(chebyshev(a, b) == 1 for a, b in zip(path, path[1:])), "путь рвётся"
    assert not (set(path) & scen.blocked), "путь прошёл сквозь непроходимый объект"
    print("дейкстра: обход стены, клеток %d" % len(path))

    budget = 8
    targets = split_turns(scen, path, budget)
    assert targets and targets[-1] == (9, 3), targets
    spent, last = 0, path[0]
    idx = 0
    for tile in path[1:]:                 # ни один ход не превышает бюджет
        step = STEP_COST[scen.ground(*tile)]
        if spent + step > budget:
            assert last == targets[idx], (last, targets[idx])
            idx += 1
            spent = step
        else:
            spent += step
        last = tile
    print("нарезка: ходов %d при бюджете %d, цели %s" % (len(targets), budget, targets))

    straight = dijkstra(scen, (0, 0), (0, 5))
    assert len(straight) == 6, straight
    print("прямой путь: клеток %d" % len(straight))
    print("selftest: ок")
    return 0


# ------------------------------------------------------------------------ cli

def parse_xy(text: str) -> tuple[int, int]:
    x, _, y = text.partition(",")
    return int(x), int(y)


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    c = sub.add_parser("check", help="проверить карту по требованиям плана Ф5")
    c.add_argument("--scenario", type=Path, required=True)
    c.add_argument("--route", type=Path, default=None,
                   help="route.json: добавляет проверки относительно маршрута")
    c.add_argument("--minimal", action="store_true",
                   help="проверять только минимально достаточное для С4 (см. Report); "
                        "деревня, руина, лавка, наёмники, наставник, мешок и события "
                        "печатаются как «отложено»")

    ro = sub.add_parser("route", help="построить маршрут по путевым точкам")
    ro.add_argument("--scenario", type=Path, required=True)
    ro.add_argument("--waypoints", nargs="+", required=True, metavar="X,Y")
    ro.add_argument("--out", type=Path, required=True)
    ro.add_argument("--move", type=int, default=None,
                    help="бюджет очков движения на ход (по умолчанию MOVE своего отряда "
                         "из .sg, а если он 0 — %d)" % DEFAULT_MOVE)
    ro.add_argument("--checkpoint-every", type=int, default=10,
                    help="сохранение игры каждые N ходов (default: 10)")

    sub.add_parser("selftest", help="проверка маршрутизации на синтетической сетке, без карты")

    args = p.parse_args()
    if args.cmd == "selftest":
        return selftest()
    if not args.scenario.exists():
        raise SystemExit("нет файла %s — карта генерируется: python tools/make_c4_map.py"
                         % args.scenario)
    if args.cmd == "check":
        return check(args.scenario, args.route, args.minimal)

    scen = Scenario(args.scenario)
    own = scen.own_stacks()
    if not own:
        raise SystemExit("в сценарии нет отряда игрока — маршрут вести нечем")
    stack = own[0]
    budget = args.move or int(stack["MOVE"]) or DEFAULT_MOVE
    waypoints = [parse_xy(w) for w in args.waypoints]
    start = (int(stack["POS_X"]), int(stack["POS_Y"]))
    gate = None
    if stack.get("INSIDE", sg.NULL_ID) != sg.NULL_ID:
        # Отряд внутри столицы: игра выпускает его через ворота — правый нижний угол за
        # прямоугольником крепости (живой прогон 07.09.2026: столица (5,5) размером 5 → отряд
        # встал в (10,10); совпадает с getFortOrRuinEntrance, task-8-report). Маршрут начинается
        # от ворот, а первый ход — сам выход из столицы.
        gate = (start[0] + CAPITAL_SIZE, start[1] + CAPITAL_SIZE)
        start = gate
    if waypoints[0] != start:
        waypoints.insert(0, start)       # маршрут всегда начинается там, где стоит отряд
    route = build_route(scen, waypoints, budget, stack["STACK_ID"], args.checkpoint_every)
    if gate is not None:
        route["turns"] = [{"turn": 1, "target": [gate[0], gate[1]], "note": "выход из столицы"}] +             [{**t, "turn": t["turn"] + 1} for t in route["turns"]]
        every = args.checkpoint_every
        route["checkpoints"] = [n for n in range(every, len(route["turns"]) + 1, every)]
    args.out.write_text(json.dumps(route, ensure_ascii=False, indent=2), encoding="utf-8")
    print("отряд %s из (%d,%d), бюджет %d очков" % (stack["STACK_ID"], start[0], start[1], budget))
    print("клеток в пути %d, ходов %d, контрольные точки %s"
          % (len(route["path"]), len(route["turns"]), route["checkpoints"]))
    for t in route["turns"]:
        x, y = t["target"]
        print("  ход %2d -> (%2d,%2d) %s" % (t["turn"], x, y, GROUND.get(scen.ground(x, y), "?")))
    print("записано в %s" % args.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
