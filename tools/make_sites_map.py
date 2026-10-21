#!/usr/bin/env python3
"""Карта M1 «sites»: столица Империи и четыре объекта вокруг неё — Торговец, Тренер,
Башня мага, Наёмник. Для сценариев S1…S4 плана `2026-09-09-f7-run-scenarios.md`.

    python tools/make_sites_map.py
    python tools/make_sites_map.py --selftest   # сторожа проверки подсадной ошибкой
    python tools/make_sites_map.py --goods      # только таблица товаров с ценами

**Зачем.** Ни одного пути списания золота в порте нет вовсе (`sites.md` §4), а формулы цен
взяты дизассемблированием и ни разу не сверены с игрой. Карта нужна, чтобы пользователь
сходил в четыре объекта и назвал, сколько списалось.

**Что на карте.** Столица в (5,5) — пятно 5x5, вокруг четыре объекта пятнами 3x3, каждый в
двух клетках от края столицы (ближе ваниль лавки к городам не ставит — см. `GAP_MIN`). Врагов
нет: `drop_neutral_stacks(keep=0)` сносит все восемнадцать отрядов шаблона. Жил
(`CMidCrystal`) нет ни одной — это требование плана §0.4.

**Почему раскладку пересобрали (09.09.2026).** В первой сборке объекты стояли в четырёх
клетках от края столицы, и играть было нельзя: «здания далеко стоят и входы направлены от
столицы, нужно частично оббегать; всё за один день — не хватает очков передвижения». Обе
причины разобраны числами.

1. *Сторона входа записью не задаётся вовсе.* `CMidSite::getEntrancePoint` — пятая запись
   таблицы виртуальных функций (`midsite.h:58`), и у всех четырёх классов лавок это одна и та
   же функция `0x00600d63` (Gog, 4 474 880; RTTI-локаторы `0x0071dfe8` Merchant, `0x0071e1a0`
   Mage, `0x0071e110` Mercs, `0x007213a0` Trainer): `mov eax,[ecx+0xc]; mov edx,[ecx+0x14];
   lea edx,[eax+edx-1]` и то же по второй оси — то есть `(POS_X + sizeX − 1,
   POS_Y + sizeY − 1)`, **правый нижний угол пятна** (раскладка `IMapElement`: `+0xc` —
   позиция, `+0x14` — размер, `mapelement.h:33-38`). Повернуть вход нечем; единственный рычаг —
   куда поставить само пятно. Поэтому здесь все четыре пятна стоят так, чтобы правый нижний
   угол смотрел на выход из столицы (у неё вход считается так же — `(9,9)`), а не прятался за
   корпусом здания.

2. *Очков хода у стартового героя 20* — `Gunits.MOVE`, минимум по лидерам Империи (Pegasus
   Knight Полководца и Archmage Мага по 20). Шаг по равнине без дороги стоит **2**
   (`computeMovementCost 0x6027f3`, ветка `0x6029d3`), значит на день ровно 10 шагов. На саму
   клетку входа встать нельзя — она часть непроходимого пятна лавки (`interactiveObjectTypes`
   содержит `SI`), отряд останавливается **рядом** с ней (`movepathhooks.cpp:101-105`:
   действие срабатывает, когда конец пути отличается от входа не больше чем на клетку). Круг
   «выйти из столицы — дойти — вернуться» здесь считается Дейкстрой и обязан уложиться в 70 %
   очков хода; сторож в `verify` падает, если нет.

**Обойти все четыре за один ход нельзя ни при какой раскладке.** Полный перебор мест, где круг
до входа укладывается в очки хода и пятно не закрывает собственный вход: при разрешённом
расстоянии 2-4 клетки таких мест ровно 12, годных четвёрок 4, у всех круг 12 очков и обход
36 очков; даже если разрешить ставить пятна вплотную к столице (чего ваниль не делает), лучший
обход — 30 очков при 20 доступных. Отсюда памятка прогона — два дня, а не один.

**Про доход. Пустая карта без жил НЕ даёт нулевого дохода.** `computePlayerDailyIncome`
(0x5da55f, разбор в `src/D2.Rules/Map/Economy.cs`) кроме жил всегда прибавляет
`Grace[раса].INCOME`, а у Империи это **100 золота и 25 маны в день**, и весь итог ещё
домножается на `1 + GVars.INCOME_{E|A|H|V}/100` (на «лёгкой» это +30 %, на остальных 0).
Убрать это сценарием нечем — столица обязана быть. Отсюда правило прогона: **все покупки
делать внутри одного хода**, а если ход всё же кончился — вычесть 100 (или 130) золота.

**Казна 9999, а не 30000, как в плане.** Валюта банка — `int16`, зажатый в [0, 9999]
(`0x586642`, `Economy.CurrencyMax`), и само поле `BANK` в `.sg` четырёхзначное
(`G9999:R0000:…`). 30000 записать нечем. Всей закупки ниже хватает на ~3800 золота.

**`MAX_UNIT` = 5.** Вопреки имени это потолок уровня ПОСТРОЙКИ (план §0.5); у шаблона он 2,
и из-за этого на прежних картах не строилась Белая башня. Заодно поднят `MAX_SPELL`: у
шаблона он 2, а в лавке лежат заклинания 3 и 5 уровня.

**Отряда сценарий не задаёт.** Игра пересоздаёт игрока при выборе лорда и стирает состав
отряда (план §0.3), так что оруженосец для S4 будет тот, что даст сама игра.

Раскладка записей — `tools/ksy/sg.ksy` (`site`, `mid_site_merchant`, `mid_site_mage`,
`mid_site_mercs`, `mid_site_trainer`); писатель сверен с ванильными байтами 150 объектов из
`game-dev/Exports-archive` (`--selftest`).
"""
from __future__ import annotations

import argparse
import heapq
import sys
import tempfile
from collections import deque
from dataclasses import dataclass, replace
from itertools import permutations
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402
import make_c4_map as c4  # noqa: E402
import scenario_c4 as sc  # noqa: E402
from dbf_dump import read as read_dbf  # noqa: E402
from make_arena_map import set_bank  # noqa: E402
from make_city_map import Checks, gap  # noqa: E402  (счётчик сравнений и чебышёв между пятнами)

MODDING = Path(__file__).resolve().parents[2] / "disciples-modding"
# Игра читает сценарии ТОЛЬКО из Exports; game-dev/scenarios — подсобка, на этом уже спотыкались
OUT = MODDING / "game-dev" / "Exports" / "sites.sg"
ARCHIVE = MODDING / "game-dev" / "Exports-archive"
GLOBALS = MODDING / "game-pristine" / "Globals"   # побайтово совпадает с game-dev/Globals
NAME = "sites"

WHAT_MERCHANT = ".?AVCMidSiteMerchant@@"
WHAT_MAGE = ".?AVCMidSiteMage@@"
WHAT_MERCS = ".?AVCMidSiteMercs@@"
WHAT_TRAINER = ".?AVCMidSiteTrainer@@"
SITE_CLS = "SI"                      # класс id объектов: S143SI0000
SITE_TILES = 3                       # след объекта 3x3 — проверено по планам ванильных карт

BANK = "G9999:R0000:Y0000:E0000:W0000:B0000"
MAX_UNIT_WANTED = 5                  # ScenarioInfo.MAX_UNIT — потолок уровня ПОСТРОЙКИ
MAX_SPELL_WANTED = 5                 # потолок уровня заклинания: в лавке лежит заклинание 5 ур.
# 2 — минимум, засвидетельствованный ванилью: по 33 картам `Exports-archive` ни одна лавка не
# стоит к городу ближе чем в двух клетках (130 пар, минимум ровно 2). Ставить вплотную нечем
# доказать — игру не запускаем, — а лишние 4 очка хода круг выдерживает. 4 — дальше уже не дойти.
GAP_MIN, GAP_MAX = 2, 4

RACE_EMPIRE = "g000rr0000"
PLAIN_COST = 2                       # цена шага по равнине без дороги (0x6027f3, ветка 0x6029d3)
RESERVE_PCT = 30                     # требуемый запас очков хода на круге столица-вход-столица

BUY_FLAGS = ("BUY_ARMOR", "BUY_JEWEL", "BUY_WEAPON", "BUY_BANNER",
             "BUY_POTION", "BUY_SCROLL", "BUY_WAND", "BUY_VALUE")
SITE_HEAD = ("SITE_ID:s IMG_ISO:i IMG_INTF:s TXT_TITLE:s TXT_DESC:s "
             "POS_X:i POS_Y:i VISITER:s AIPRIORITY:i")


@dataclass(frozen=True)
class SiteSpec:
    """Что поставить: класс записи, картинка, надписи, левый верхний угол пятна, нагрузка."""
    what: str
    iso: int                          # IMG_ISO — индекс картинки; 0 встречается у всех четырёх
    title: str
    desc: str
    xy: tuple[int, int]
    payload: tuple = ()


# Товар подобран так, чтобы каждый сценарий было чем считать: у Торговца четыре предмета
# разной цены (в том числе «ценность» — у неё отдельная ветка текста X005TA0589), у Наёмника
# два юнита с ценами 30 и 300, у Башни мага заклинания 1, 3 и 5 уровня по 200/600/1000.
MERCHANT_ITEMS = (("G000IG0005", 1), ("G000IG0002", 1), ("G000IG0003", 1), ("G000IG7002", 1))
MAGE_SPELLS = ("G000SS0001", "G000SS0011", "G000SS0018")
MERCS_UNITS = (("G000UU5001", 1, 0), ("G000UU5039", 1, 0))   # (тип, уровень, единственный)

# Места выбраны полным перебором: у каждого правый нижний угол пятна — вход — смотрит на выход
# из столицы (9,9), пятно не закрывает собственный вход, круг «столица - вход - столица» стоит
# 12 очков хода из 20 (запас 40 %). Порядок — порядок дешёвого обхода.
SITES = (
    SiteSpec(WHAT_TRAINER, 0, "Тренер", "Кого тренировать?", (11, 3)),
    SiteSpec(WHAT_MERCHANT, 0, "Торговец", "Мои товары помогут вам.", (11, 8), MERCHANT_ITEMS),
    SiteSpec(WHAT_MERCS, 0, "Наемник", "Нужны рекруты?", (8, 11), MERCS_UNITS),
    SiteSpec(WHAT_MAGE, 0, "Башня мага", "Заклинания по разумной цене.", (4, 11), MAGE_SPELLS),
)


# ------------------------------------------------------------------ геометрия

def site_tiles(xy: tuple[int, int]) -> list[tuple[int, int]]:
    x0, y0 = xy
    return [(x0 + dx, y0 + dy) for dy in range(SITE_TILES) for dx in range(SITE_TILES)]


def entrance(xy: tuple[int, int]) -> tuple[int, int]:
    """Точка входа — правый нижний угол следа: `(POS_X + sizeX − 1, POS_Y + sizeY − 1)`.

    Ранг 1: `CMidSite::getEntrancePoint` = `0x00600d63` у всех четырёх классов лавок (пятая
    запись таблицы, `midsite.h:58`; таблицы найдены по RTTI-локаторам `0x0071dfe8`,
    `0x0071e1a0`, `0x0071e110`, `0x007213a0`). Тело — шесть инструкций, читающих `[ecx+0xc]`
    (позиция) и `[ecx+0x14]` (размер) и вычитающих единицу. **Никакого поля «сторона входа» в
    записи объекта нет** — развернуть вход к столице можно только выбором места пятна.
    """
    return xy[0] + SITE_TILES - 1, xy[1] + SITE_TILES - 1


def entrance_of_capital() -> tuple[int, int]:
    """Выход из столицы — тот же правый нижний угол её пятна 5x5 (`0x4cd253`, ранг 1; отряд
    «внутри» города движок считает стоящим именно там — сверено по журналу C4, `can_move` 95073).
    Из города отряд выходит только на соседей ЭТОЙ клетки, а не на всё кольцо вокруг пятна."""
    x0, y0 = c4.CAPITAL_XY
    return x0 + c4.CAPITAL_SIZE - 1, y0 + c4.CAPITAL_SIZE - 1


def around(cells: list[tuple[int, int]], size: int) -> set[tuple[int, int]]:
    """Кольцо соседей пятна (8 направлений), сами клетки пятна исключены."""
    inside = set(cells)
    return {(x + dx, y + dy) for x, y in cells for dx in (-1, 0, 1) for dy in (-1, 0, 1)
            if 0 <= x + dx < size and 0 <= y + dy < size and (x + dx, y + dy) not in inside}


def wanted_tiles(size: int, walled: tuple[int, int] | None = None) -> dict:
    """Вся карта — равнина, поверх неё пятно столицы. `walled` — подсадная стена гор вокруг
    объекта: ею `--selftest` доказывает, что сторож достижимости не молчит."""
    tiles = {(x, y): c4.TILE[0] for y in range(size) for x in range(size)}
    for x, y in c4.capital_tiles():
        tiles[(x, y)] = c4.TILE_CAPITAL
    for x, y in around(site_tiles(walled), size) if walled else ():
        tiles[(x, y)] = c4.TILE[4]
    return tiles


# ------------------------------------------------------- писатель записей объектов

def site_head(sid: str, iso: int, title: str, desc: str, x: int, y: int,
              visiter: str = sg.NULL_ID, prio: int = 3) -> bytes:
    """Общая шапка сайта (`sg.ksy: site`). `IMG_INTF` у всех 150 ванильных объектов пуст,
    `AIPRIORITY` у всех 3 — так и пишем."""
    return (sg.f_str("SITE_ID", sid) + sg.f_int("IMG_ISO", iso) + sg.f_str("IMG_INTF", "")
            + sg.f_str("TXT_TITLE", title) + sg.f_str("TXT_DESC", desc)
            + sg.f_int("POS_X", x) + sg.f_int("POS_Y", y)
            + sg.f_str("VISITER", visiter) + sg.f_int("AIPRIORITY", prio))


def visitors_tail(sid: str, visitors: list[tuple[str, str]] | None = None) -> bytes:
    """Хвост любого сайта: счётчик посетителей и список пар {SITE_ID, VISITER}."""
    visitors = visitors or []
    return sg.f_count(sid, len(visitors)) + b"".join(
        sg.f_str("SITE_ID", a) + sg.f_str("VISITER", b) for a, b in visitors)


def merchant_body(head: bytes, sid: str, items, flags=(1,) * 8, mission: int = 0,
                  visitors=None) -> bytes:
    return (head + b"".join(sg.f_bool(f, v) for f, v in zip(BUY_FLAGS, flags))
            + sg.f_int("QTY_ITEM", len(items))
            + b"".join(sg.f_str("ITEM_ID", i) + sg.f_int("ITEM_COUNT", n) for i, n in items)
            + sg.f_bool("MISSION", mission) + visitors_tail(sid, visitors))


def mage_body(head: bytes, sid: str, spells, visitors=None) -> bytes:
    return (head + sg.f_int("QTY_SPELL", len(spells))
            + b"".join(sg.f_str("SPELL_ID", s) for s in spells)
            + visitors_tail(sid, visitors))


def mercs_body(head: bytes, sid: str, units, visitors=None) -> bytes:
    return (head + sg.f_int("QTY_UNIT", len(units))
            + b"".join(sg.f_str("UNIT_ID", u) + sg.f_int("UNIT_LEVEL", lv)
                       + sg.f_bool("UNIT_UNIQ", uq) for u, lv, uq in units)
            + visitors_tail(sid, visitors))


def trainer_body(head: bytes, sid: str, _payload=(), visitors=None) -> bytes:
    """У Тренера нагрузки нет вовсе — только шапка и посетители (`sg.ksy:895-904`)."""
    return head + visitors_tail(sid, visitors)


BODY_WRITER = {WHAT_MERCHANT: merchant_body, WHAT_MAGE: mage_body,
               WHAT_MERCS: mercs_body, WHAT_TRAINER: trainer_body}


def site_body(what: str, sid: str, spec: SiteSpec) -> bytes:
    head = site_head(sid, spec.iso, spec.title, spec.desc, *spec.xy)
    return BODY_WRITER[what](head, sid, spec.payload)


# ------------------------------------------------------------------ читатель

def parse_site(what: str, body: bytes) -> dict:
    """Разобрать тело объекта до последнего байта; недочитанный хвост — ошибка раскладки."""
    c = sg.Cursor(body)
    out: dict = c.fields(SITE_HEAD)
    if what == WHAT_MERCHANT:
        out.update(c.fields(" ".join(f + ":b" for f in BUY_FLAGS)))
        out["ITEMS"] = [(c.str("ITEM_ID"), c.int("ITEM_COUNT"))
                        for _ in range(c.int("QTY_ITEM"))]
        out["MISSION"] = c.bool("MISSION")
    elif what == WHAT_MAGE:
        out["SPELLS"] = [c.str("SPELL_ID") for _ in range(c.int("QTY_SPELL"))]
    elif what == WHAT_MERCS:
        out["UNITS"] = [(c.str("UNIT_ID"), c.int("UNIT_LEVEL"), c.bool("UNIT_UNIQ"))
                        for _ in range(c.int("QTY_UNIT"))]
    out["VISITORS"] = [(c.str("SITE_ID"), c.str("VISITER")) for _ in range(c.count())]
    if c.pos != len(body):
        raise SystemExit("тело %s разобрано не до конца: %d из %d" % (what, c.pos, len(body)))
    return out


def rebuild_site(what: str, rec: sg.Record) -> bytes:
    """Собрать тело заново из полей, прочитанных у него же, — для сверки с ванилью."""
    f = parse_site(what, rec.body)
    head = site_head(rec.obj_id, int(f["IMG_ISO"]), str(f["TXT_TITLE"]), str(f["TXT_DESC"]),
                     int(f["POS_X"]), int(f["POS_Y"]), str(f["VISITER"]), int(f["AIPRIORITY"]))
    if what == WHAT_MERCHANT:
        return merchant_body(head, rec.obj_id, f["ITEMS"],
                             tuple(f[k] for k in BUY_FLAGS), int(f["MISSION"]), f["VISITORS"])
    if what == WHAT_MAGE:
        return mage_body(head, rec.obj_id, f["SPELLS"], f["VISITORS"])
    if what == WHAT_MERCS:
        return mercs_body(head, rec.obj_id, f["UNITS"], f["VISITORS"])
    return trainer_body(head, rec.obj_id, (), f["VISITORS"])


def add_sites(scen: sg.Sg, specs: tuple[SiteSpec, ...]) -> tuple[sg.Sg, list[str]]:
    ids = scen.next_ids(SITE_CLS, len(specs))
    for sid, spec in zip(ids, specs):
        body = site_body(spec.what, sid, spec)
        parse_site(spec.what, body)        # собранное обязано читаться нашим же курсором
        scen = scen.appended(sg.Record(spec.what, sid, body))
    return scen, ids


# --------------------------------------------------------------- ScenarioInfo

_INFO_LAYOUT = ("INFO_ID:s CAMPAIGN:s SOURCE_M:b QTY_CITIES:i NAME:s DESC:s BRIEFING:s "
                "DEBUNKW:s DEBUNKW2:s DEBUNKW3:s DEBUNKW4:s DEBUNKW5:s DEBUNKL:s "
                "BRIEFLONG1:s BRIEFLONG2:s BRIEFLONG3:s BRIEFLONG4:s BRIEFLONG5:s O:b "
                "CUR_TURN:i MAX_UNIT:i MAX_SPELL:i MAX_LEADER:i MAX_CITY:i MAP_SIZE:i")


def info_layout(scen: sg.Sg) -> str:
    """В формате MidFile 34 (не Rise of the Elves) четырёх запасных `DEBUNKW` нет."""
    if scen.header[:10].split(b"\x00")[0] == b"D2EESFISIG":
        return _INFO_LAYOUT
    return _INFO_LAYOUT.replace(" DEBUNKW2:s DEBUNKW3:s DEBUNKW4:s DEBUNKW5:s", "")


def info_fields(scen: sg.Sg) -> dict:
    return sg.fields(scen.one(sg.WHAT_INFO).body, info_layout(scen))


def set_info_int(scen: sg.Sg, name: str, value: int) -> sg.Sg:
    """Переписать одно целое поле `ScenarioInfo`: курсор доходит до него по раскладке,
    поле пересобирается, всё вокруг копируется байт в байт."""
    lay = info_layout(scen)
    if name + ":i" not in lay.split():
        raise SystemExit("поля %s нет в раскладке ScenarioInfo" % name)
    rec = scen.one(sg.WHAT_INFO)
    c = sg.Cursor(rec.body)
    c.fields(lay.split(name + ":i")[0])
    start = c.pos
    c.int(name)
    body = rec.body[:start] + sg.f_int(name, value) + rec.body[c.pos:]
    return scen.with_record(rec, replace(rec, body=body))


# ------------------------------------------------------------------ достижимость

def obstacle_cells(scen: sg.Sg) -> set[tuple[int, int]]:
    """Клетки непроходимых объектов: столица и сайты. Отряды не мешают — герой стоит на своей."""
    stacks = {r.obj_id for r in scen.find(sg.WHAT_STACK)}
    _, plan = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    return {(x, y) for x, y, e in plan if e not in stacks}


def reachable(scen: sg.Sg, grid: list[list[int]]) -> set[tuple[int, int]]:
    """Обход в ширину по проходимой равнине от клеток вокруг столицы, 8 направлений."""
    size = len(grid)
    blocked = obstacle_cells(scen)

    def free(cell: tuple[int, int]) -> bool:
        x, y = cell
        return cell not in blocked and sg.passable(grid[y][x])

    seen = {c for c in around(c4.capital_tiles(), size) if free(c)}
    queue = deque(seen)
    while queue:
        x, y = queue.popleft()
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                nxt = (x + dx, y + dy)
                if (0 <= nxt[0] < size and 0 <= nxt[1] < size
                        and nxt not in seen and free(nxt)):
                    seen.add(nxt)
                    queue.append(nxt)
    return seen


# ------------------------------------------------------------------- очки хода

def hero_move() -> int:
    """Очки хода стартового героя — минимум `Gunits.MOVE` по лидерам Империи.

    Какого героя даст выбор лорда, сценарий не решает (игра пересоздаёт игрока), поэтому
    берётся худший случай по всем пяти лидерам расы; сейчас это 20 — столько у Pegasus Knight
    (Полководец) и у Archmage (Маг). Число читается из таблицы, а не выписывается руками.
    """
    rows = read_dbf(GLOBALS / "Gunits.dbf")["rows"]
    moves = [int(r["MOVE"]) for r in rows
             if r["RACE_ID"].lower() == RACE_EMPIRE and int(r["LEADER_CAT"]) > 0
             and int(r["MOVE"]) > 0]
    if not moves:
        raise SystemExit("в Gunits.dbf нет лидеров Империи — очки хода брать неоткуда")
    return min(moves)


def walk_costs(blocked: set, size: int, starts: dict) -> dict:
    """Дейкстра по равнине: восемь направлений, шаг стоит `PLAIN_COST`, пятна непроходимы.

    `starts` — начальная цена в каждой клетке-источнике (у столицы это уже цена первого шага
    наружу, у стоянки рядом с лавкой — ноль, отряд там уже стоит).
    """
    dist = dict(starts)
    queue = [(c, xy) for xy, c in starts.items()]
    heapq.heapify(queue)
    while queue:
        cost, (x, y) = heapq.heappop(queue)
        if cost > dist[(x, y)]:
            continue
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                nxt = (x + dx, y + dy)
                if (nxt == (x, y) or not (0 <= nxt[0] < size and 0 <= nxt[1] < size)
                        or nxt in blocked or cost + PLAIN_COST >= dist.get(nxt, 1 << 30)):
                    continue
                dist[nxt] = cost + PLAIN_COST
                heapq.heappush(queue, (cost + PLAIN_COST, nxt))
    return dist


def standing(cell: tuple[int, int], blocked: set, size: int) -> set:
    """Где отряд может стоять, чтобы «дотянуться» до клетки `cell`: свободные соседи.

    На сам вход встать нельзя — это клетка непроходимого пятна (`interactiveObjectTypes`
    содержит `SI`); движок засчитывает действие, когда конец пути отличается от входа не
    больше чем на клетку по каждой оси (`movepathhooks.cpp:101-105`).
    """
    return around([cell], size) - blocked


def round_trip(blocked: set, size: int, xy: tuple[int, int]) -> int | None:
    """Очки хода на «выйти из столицы, дойти до входа объекта и вернуться в столицу».

    Цена симметрична: из столицы первый шаг наружу стоит `PLAIN_COST`, обратно последний шаг
    внутрь стоит столько же, поэтому круг — ровно удвоенная дорога туда. `None` — не дойти.
    """
    porch = standing(entrance_of_capital(), blocked, size)
    dist = walk_costs(blocked, size, {c: PLAIN_COST for c in porch})
    reach = [dist[c] for c in standing(entrance(xy), blocked, size) if c in dist]
    return 2 * min(reach) if reach else None


def next_stop(cost: dict, frm: dict, cells: set) -> dict:
    """Во сколько обойдётся приход в каждую клетку следующей стоянки — из любой клетки, где
    отряд мог оказаться на предыдущей. Недостижимые клетки выпадают из словаря."""
    out = {}
    for c in cells:
        reach = [was + frm[a][c] for a, was in cost.items() if c in frm[a]]
        if reach:
            out[c] = min(reach)
    return out


def tour(blocked: set, size: int, specs: tuple[SiteSpec, ...]) -> tuple[int, tuple] | None:
    """Самый дешёвый обход всех объектов с выходом из столицы и возвратом в неё, в очках хода.

    Считается по КЛЕТКАМ, а не по объектам: у каждого входа несколько мест, где можно встать,
    и отряд не может уйти не с той клетки, на которую пришёл, — поэтому динамика по маршруту
    ведёт конкретную клетку, а не «объект целиком».
    """
    porch = standing(entrance_of_capital(), blocked, size)
    stand = {s.title: standing(entrance(s.xy), blocked, size) for s in specs}
    if not porch or not all(stand.values()):
        return None
    out = walk_costs(blocked, size, {c: PLAIN_COST for c in porch})    # цена пути из столицы
    frm = {c: walk_costs(blocked, size, {c: 0}) for cells in stand.values() for c in cells}
    home = {c: min((frm[c][r] + PLAIN_COST for r in porch if r in frm[c]), default=None)
            for c in frm}
    best = None
    for order in permutations(stand):
        cost = {c: out[c] for c in stand[order[0]] if c in out}
        for name in order[1:]:
            cost = next_stop(cost, frm, stand[name])
        end = min((v + home[c] for c, v in cost.items() if home[c] is not None), default=None)
        if end is not None and (best is None or end < best[0]):
            best = (end, ("столица",) + order + ("столица",))
    return best


# ------------------------------------------------------------------------ сборка

SABOTAGE = ("nosite", "emptyshop", "crystal", "maxunit", "far", "wall", "turn", "budget")

# Порча «turn» — место Башни мага из первой сборки: пятно встаёт между столицей и собственным
# входом, и до входа приходится оббегать здание. Порча «budget» — место, где оббегать не надо
# и до столицы близко (gap 4), но круг стоит 20 очков из 20, то есть запаса нет вовсе.
TURNED_XY = (13, 11)
FARISH_XY = (13, 2)


def build(template: Path, name: str = NAME, sabotage: str = "") -> tuple[sg.Sg, dict]:
    """Собрать карту. `sabotage` — подсадная порча ровно одного обещания, для `--selftest`."""
    if sabotage and sabotage not in SABOTAGE:
        raise SystemExit("неизвестная порча %s" % sabotage)
    specs = sabotaged_specs(sabotage)
    scen = sg.Sg.read(template)
    size = len(sg.tile_grid(scen))
    walled = SITES[1].xy if sabotage == "wall" else None
    scen, changed = c4.set_terrain(scen, wanted_tiles(size, walled))
    scen = c4.move_capital(scen, *c4.CAPITAL_XY)

    hero = next(r for r in scen.find(sg.WHAT_STACK)
                if sg.parse_stack(r.body)["INSIDE"] != sg.NULL_ID)
    scen = c4.move_stack(scen, hero, *c4.CAPITAL_XY)
    scen, dropped = c4.drop_neutral_stacks(scen, keep=0)
    if sabotage == "crystal":
        scen, _ = c4.add_crystals(scen, [(0, 12, 20)])

    human = next(sg.fields(r.body, sg.LAYOUT_PLAYER) for r in scen.find(sg.WHAT_PLAYER)
                 if sg.fields(r.body, sg.LAYOUT_PLAYER)["RACE_ID"] != sc.RACE_NEUTRAL)
    scen = set_bank(scen, human["PLAYER_ID"], BANK)
    scen = set_info_int(scen, "MAX_UNIT", 2 if sabotage == "maxunit" else MAX_UNIT_WANTED)
    scen = set_info_int(scen, "MAX_SPELL", MAX_SPELL_WANTED)

    scen, ids = add_sites(scen, specs)
    scen = c4.rebuild_plan(scen)                  # столица, отряды и (при порче) жилы
    scen = sg.add_plan_entries(scen, [(x, y, sid) for sid, spec in zip(ids, specs)
                                      for x, y in site_tiles(spec.xy)])
    scen = sg.set_scenario_name(scen, name)
    return scen, {"клеток перерисовано": changed, "отрядов удалено": len(dropped),
                  "объектов добавлено": len(ids), "казна": BANK.split(":")[0],
                  "записей": len(scen.records)}


def sabotaged_specs(sabotage: str) -> tuple[SiteSpec, ...]:
    """Список объектов с ровно одной подсадной порчей — остальное как в `SITES`."""
    if sabotage == "nosite":
        return tuple(s for s in SITES if s.what != WHAT_MERCS)
    if sabotage == "emptyshop":
        return tuple(replace(s, payload=()) if s.what == WHAT_MERCHANT else s for s in SITES)
    if sabotage == "far":
        return tuple(replace(s, xy=(40, 40)) if s.what == WHAT_MAGE else s for s in SITES)
    if sabotage == "turn":
        return tuple(replace(s, xy=TURNED_XY) if s.what == WHAT_MAGE else s for s in SITES)
    if sabotage == "budget":
        return tuple(replace(s, xy=FARISH_XY) if s.what == WHAT_TRAINER else s for s in SITES)
    return SITES


# ------------------------------------------------------------------ цены из таблиц

def _gold(bank: str) -> int:
    """Золото из строки банка вида `g0450:r0000:…` — первое поле, четыре цифры."""
    return int(bank.split(":")[0][1:])


def goods_rows() -> list[tuple[str, str, str, int, str]]:
    """Таблица товаров: (объект, id, что это, цена в золоте, примечание). Читается из
    `Globals/*.dbf`, а не выписывается руками, — иначе памятка разойдётся с игрой."""
    items = {r["ITEM_ID"].upper(): r for r in read_dbf(GLOBALS / "GItem.DBF")["rows"]}
    spells = {r["SPELL_ID"].upper(): r for r in read_dbf(GLOBALS / "Gspells.dbf")["rows"]}
    units = {r["UNIT_ID"].upper(): r for r in read_dbf(GLOBALS / "Gunits.dbf")["rows"]}
    texts = {r["TXT_ID"].upper(): r["TEXT"] for r in read_dbf(GLOBALS / "Tglobal.dbf")["rows"]}
    ratio = read_dbf(GLOBALS / "GVars.DBF")["rows"][0]["SELL_RATIO"]
    rows = []
    for iid, _n in MERCHANT_ITEMS:
        v = _gold(items[iid]["VALUE"])
        rows.append(("Торговец", iid, texts.get(items[iid]["NAME_TXT"].upper(), "?"), v,
                     "продажа обратно %d (%d %%)" % (v * ratio // 100, ratio)))
    for sid in MAGE_SPELLS:
        s = spells[sid]
        rows.append(("Башня мага", sid, texts.get(s["NAME_TXT"].upper(), "?"),
                     _gold(s["BUY_C"]), "уровень заклинания %d" % s["LEVEL"]))
    for uid, _lv, _uq in MERCS_UNITS:
        u = units[uid]
        rows.append(("Наемник", uid, texts.get(u["NAME_TXT"].upper(), "?"),
                     _gold(u["ENROLL_C"]), "HP %d, до уровня %d опыта" % (u["HIT_POINT"],
                                                                         u["XP_NEXT"])))
    return rows


def print_layout(scen: sg.Sg) -> None:
    """Раскладка числами: где пятна, где входы и во что обходится дорога к каждому."""
    size = len(sg.tile_grid(scen))
    blocked = obstacle_cells(scen)
    move = hero_move()
    cap = c4.capital_tiles()
    print("столица %s (5x5), выход из неё %s; очков хода у героя %d, шаг по равнине %d"
          % (c4.CAPITAL_XY, entrance_of_capital(), move, PLAIN_COST))
    print("  %-11s %-9s %-8s %6s %6s %7s" % ("объект", "пятно", "вход", "клеток", "круг", "запас"))
    for spec in SITES:
        trip = round_trip(blocked, size, spec.xy)
        print("  %-11s %-9s %-8s %6d %6s %6s"
              % (spec.title, spec.xy, entrance(spec.xy), gap(cap, site_tiles(spec.xy)),
                 trip if trip is not None else "нет",
                 "%d %%" % round(100 * (move - trip) / move) if trip is not None else "-"))
    best = tour(blocked, size, SITES)
    if best:
        print("  обход всех четырёх: %d очков (%s) — это %d хода при %d очках в день"
              % (best[0], " -> ".join(best[1]), -(-best[0] // move), move))


def print_goods() -> None:
    print("%-11s %-11s %-26s %6s  %s" % ("объект", "id", "что", "золото", "примечание"))
    for site, oid, what, price, note in goods_rows():
        print("%-11s %-11s %-26s %6d  %s" % (site, oid, what[:26], price, note))


# ------------------------------------------------------------------- проверка

def verify(path: Path) -> None:
    """Прочитать записанный файл обратно и проверить числами всё, что генератор обещал."""
    scen = sg.Sg.read(path)          # чтение само сверяет счётчик объектов и рамки записей
    ch = Checks("проверка чтением")
    grid = sg.tile_grid(scen)
    size = len(grid)
    want = wanted_tiles(size)
    ch.eq("клеток с чужим грунтом", sum(grid[y][x] != t[0] for (x, y), t in want.items()), 0)

    players = [sg.fields(r.body, sg.LAYOUT_PLAYER) for r in scen.find(sg.WHAT_PLAYER)]
    human = [p for p in players if p["RACE_ID"] != sc.RACE_NEUTRAL]
    ch.eq("играбельных игроков", len(human), 1)
    ch.eq("казна игрока", human[0]["BANK"], BANK)
    stacks = [sg.parse_stack(r.body) for r in scen.find(sg.WHAT_STACK)]
    ch.eq("своих отрядов", sum(s["OWNER"] == human[0]["PLAYER_ID"] for s in stacks), 1)
    ch.eq("враждебных отрядов",
          len(stacks) - sum(s["OWNER"] == human[0]["PLAYER_ID"] for s in stacks), 0)
    ch.eq("жил на карте", len(scen.find(c4.WHAT_CRYSTAL)), 0)

    info = info_fields(scen)
    ch.eq("MAX_UNIT (потолок уровня постройки)", info["MAX_UNIT"], MAX_UNIT_WANTED)
    ch.eq("MAX_SPELL", info["MAX_SPELL"], MAX_SPELL_WANTED)
    ch.eq("имя сценария в записи", sg.scenario_name(scen), NAME)
    ch.eq("имя сценария в заголовке",
          sg.header_name(scen), NAME)

    check_sites(scen, ch, grid)
    ids = {r.obj_id for r in scen.records}
    _, plan = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    ch.eq("висячих ссылок в плане", len({e for _, _, e in plan} - ids), 0)
    # дубли клеток в плане сами по себе штатны (в ванили их 5-16 на карту: отряд стоит в городе),
    # а вот наложение НЕПРОХОДИМЫХ пятен друг на друга — нет
    solid = c4.capital_tiles() + [t for s in scen.records if s.cls == SITE_CLS
                                  for t in site_tiles(site_xy(scen, s))]
    ch.eq("клеток, где непроходимые пятна наложились", len(solid) - len(set(solid)), 0)
    ch.done()


def site_xy(scen: sg.Sg, rec: sg.Record) -> tuple[int, int]:
    f = parse_site(rec.what, rec.body)
    return int(f["POS_X"]), int(f["POS_Y"])


def check_sites(scen: sg.Sg, ch: Checks, grid: list[list[int]]) -> None:
    """Четыре объекта: каждый на месте, с товаром, рядом со столицей и достижим от неё."""
    free = reachable(scen, grid)
    _, plan = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    cap = c4.capital_tiles()
    size = len(grid)
    blocked = obstacle_cells(scen)
    budget = hero_move() * (100 - RESERVE_PCT) // 100
    for what in (WHAT_MERCHANT, WHAT_MAGE, WHAT_MERCS, WHAT_TRAINER):
        found = scen.find(what)
        ch.eq("объектов класса %s" % what.split("Site")[-1].rstrip("@"), len(found), 1)
        if not found:
            continue
        rec = found[0]
        f = parse_site(what, rec.body)
        xy = (int(f["POS_X"]), int(f["POS_Y"]))
        tiles = site_tiles(xy)
        d = gap(cap, tiles)
        ch.is_true("%s в %d клетках от столицы" % (f["TXT_TITLE"], d), GAP_MIN <= d <= GAP_MAX,
                   "вне [%d, %d]" % (GAP_MIN, GAP_MAX))
        ch.eq("клеток %s в плане" % f["TXT_TITLE"],
              len({(x, y) for x, y, e in plan if e == rec.obj_id} ^ set(tiles)), 0)
        ch.is_true("%s достижим от столицы" % f["TXT_TITLE"],
                   bool(around(tiles, len(grid)) & free), "вокруг него нет свободной клетки")
        ch.eq("точка входа %s свободна от других объектов" % f["TXT_TITLE"],
              sum(1 for x, y, e in plan if (x, y) == entrance(xy) and e != rec.obj_id), 0)
        ch.eq("посетителей у %s" % f["TXT_TITLE"], len(f["VISITORS"]), 0)
        check_walk(ch, blocked, size, xy, str(f["TXT_TITLE"]), budget)
    check_stock(scen, ch)


def check_walk(ch: Checks, blocked: set, size: int, xy: tuple[int, int],
               title: str, budget: int) -> None:
    """Два сторожа хода: вход обращён к столице и круг до него укладывается в очки хода."""
    trip = round_trip(blocked, size, xy)
    ch.is_true("до входа %s вообще есть дорога" % title, trip is not None, "входа не достать")
    if trip is None:
        return
    # «Вход обращён к столице» = собственное пятно не заставляет оббегать себя же: круг с
    # пятном обязан совпасть с кругом, посчитанным без него.
    straight = round_trip(blocked - set(site_tiles(xy)), size, xy)
    ch.eq("вход %s обращён к столице (круг %d очков, без пятна %s)"
          % (title, trip, straight), trip, straight)
    ch.is_true("круг до входа %s: %d очков хода из %d" % (title, trip, budget),
               trip <= budget, "дороже %d — запаса %d %% не остаётся" % (budget, RESERVE_PCT))


def check_stock(scen: sg.Sg, ch: Checks) -> None:
    """Товар обязан быть и обязан РАЗЛИЧАТЬСЯ ценой — иначе сценариям нечего считать."""
    prices = {(site, oid): gold for site, oid, _n, gold, _c in goods_rows()}
    for rec in scen.find(WHAT_MERCHANT):
        got = [i for i, _n in parse_site(WHAT_MERCHANT, rec.body)["ITEMS"]]
        ch.eq("предметов у Торговца", len(got), len(MERCHANT_ITEMS))
        ch.is_true("разных цен у Торговца",
                   len({prices[("Торговец", i)] for i in got}) >= 2, "меньше двух")
        ch.is_true("все флаги BUY_* у Торговца подняты",
                   all(parse_site(WHAT_MERCHANT, rec.body)[f] for f in BUY_FLAGS))
    for rec in scen.find(WHAT_MAGE):
        got = parse_site(WHAT_MAGE, rec.body)["SPELLS"]
        ch.eq("заклинаний в Башне мага", len(got), len(MAGE_SPELLS))
        ch.is_true("разных цен в Башне мага",
                   len({prices[("Башня мага", s)] for s in got}) >= 2, "меньше двух")
    for rec in scen.find(WHAT_MERCS):
        got = [u for u, _lv, _uq in parse_site(WHAT_MERCS, rec.body)["UNITS"]]
        ch.eq("юнитов у Наемника", len(got), len(MERCS_UNITS))
        ch.is_true("разных цен у Наемника",
                   len({prices[("Наемник", u)] for u in got}) >= 2, "меньше двух")


def check_writer_vs_vanilla(archive: Path = ARCHIVE) -> None:
    """Доказать писателя ванильными байтами, а не своим же курсором: каждый объект ванильных
    карт пересобирается из полей, прочитанных у него самого, и сравнивается БАЙТ В БАЙТ.
    Здесь совпасть обязаны все до одного: в шапке нет полей, которых мы бы не писали."""
    if not archive.is_dir():
        raise SystemExit("нет каталога ванильных карт %s — сверять писателя нечем" % archive)
    same: dict[str, int] = {}
    diff: dict[str, int] = {}
    for f in sorted(archive.glob("*.sg")):
        scen = sg.Sg.read(f)
        for what in (WHAT_MERCHANT, WHAT_MAGE, WHAT_MERCS, WHAT_TRAINER):
            key = what.split("Site")[-1].rstrip("@")
            for rec in scen.find(what):
                ok = rebuild_site(what, rec) == rec.body
                (same if ok else diff)[key] = (same if ok else diff).get(key, 0) + 1
    total = sum(same.values()) + sum(diff.values())
    print("писатель против ванили: воспроизведено байт в байт %d, разошлось %d (всего %d)"
          % (sum(same.values()), sum(diff.values()), total))
    for key in sorted(set(same) | set(diff)):
        print("  %-9s совпало %3d, разошлось %3d" % (key, same.get(key, 0), diff.get(key, 0)))
    if not total:
        raise SystemExit("в ванильных картах не найдено ни одного объекта — сравнений ноль")
    for what in (WHAT_MERCHANT, WHAT_MAGE, WHAT_MERCS, WHAT_TRAINER):
        key = what.split("Site")[-1].rstrip("@")
        if not same.get(key):
            raise SystemExit("ни одно ванильное тело %s не воспроизведено — раскладка неверна" % key)
    if sum(diff.values()):
        raise SystemExit("расхождений с ванилью %d — писатель неполон" % sum(diff.values()))


def selftest(template: Path) -> int:
    """Сторожа проверяются подсадной ошибкой: «упало бы» надо доказать, а не пообещать.

    Каждый случай проходит НАСТОЯЩУЮ сборку с одной испорченной величиной, пишется во
    временный файл и читается обратно тем же `verify`. Игровых каталогов не касается.
    """
    check_writer_vs_vanilla()
    cases: list[tuple[str, str | None]] = []
    with tempfile.TemporaryDirectory(prefix="d2sites") as tmp:
        out = Path(tmp) / "probe.sg"
        scen, _ = build(template)
        scen.write(out)
        try:
            verify(out)
            clean: str | None = None
        except SystemExit as e:
            clean = str(e)
        for bad in SABOTAGE:
            scen, _ = build(template, sabotage=bad)
            scen.write(out)
            try:
                verify(out)
                cases.append((bad, None))
            except SystemExit as e:
                cases.append((bad, str(e)))
    print()
    caught = sum(msg is not None for _, msg in cases)
    for bad, msg in cases:
        print("порча %-10s %s" % (bad, ("ловится: " + msg) if msg else "ПРОПУЩЕНА"))
    print("чистая сборка        %s" % ("проходит" if clean is None else "ПАДАЕТ: " + clean))
    print("selftest: сторожей сработало %d, пропущено %d из %d; чистых сборок прошло %d из 1"
          % (caught, len(cases) - caught, len(cases), int(clean is None)))
    return 0 if caught == len(cases) and clean is None else 1


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--template", type=Path, default=c4.TEMPLATE)
    p.add_argument("--out", type=Path, default=OUT)
    p.add_argument("--name", default=NAME)
    p.add_argument("--goods", action="store_true", help="только таблица товаров с ценами")
    p.add_argument("--selftest", action="store_true", help="проверить сторожей подсадной ошибкой")
    a = p.parse_args(argv)
    if a.goods:
        print_goods()
        return 0
    if not a.template.exists():
        raise SystemExit("нет шаблона %s" % a.template)
    if a.selftest:
        return selftest(a.template)
    scen, stats = build(a.template, a.name)
    a.out.parent.mkdir(parents=True, exist_ok=True)
    scen.write(a.out)
    print("%s: %s" % (a.out, ", ".join("%s %s" % kv for kv in stats.items())))
    verify(a.out)
    print_layout(scen)
    print()
    print_goods()
    return 0


if __name__ == "__main__":
    sys.exit(main())
