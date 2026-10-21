#!/usr/bin/env python3
"""Четыре синтетические карты Ф7 для ручных прогонов: M2 growth, M3 capture, M5 regen, M7 land.

    python tools/make_f7_maps.py                 # собрать все четыре в game-dev/Exports
    python tools/make_f7_maps.py --only m3       # собрать одну
    python tools/make_f7_maps.py --selftest      # сторожа проверяются подсадной ошибкой

ТЗ — `disciples-modding/docs/superpowers/plans/2026-09-09-f7-run-scenarios.md` (сценарии S5,
S6, S8, S10), разведка механик — `.superpowers/sdd/2026-09-09-f6plus-coverage/cities.md`.
Игра отсюда не запускается: сборщик только пишет `.sg` и читает их обратно.

**Что общего у всех четырёх.**

* `MAX_UNIT = 5`. Вопреки имени это потолок уровня ПОСТРОЙКИ (`cities.md`, ранг 1): движок
  сравнивает `Gbuild.LEVEL` с ним и отвечает `ExceedsMaxLevel`. В шаблоне и во всех наших
  прежних картах стоит 2 — отсюда старая жалоба «не даёт построить Белую башню» (`LEVEL 4`).
* **Жил (кристаллов) нет ни одной.** Доход идёт с жил, а карта без жил даёт нулевой доход, и
  тогда любое изменение казны — это ровно трата. На прогоне `city-hotkeys` 09.09.2026 доход и
  наём съели трату, и цену роста пришлось брать из свитка игры; второй раз так не делаем.
* **Врагов, кроме указанных в карте, нет**: `drop_neutral_stacks(keep=0)` сносит все
  восемнадцать отрядов шаблона вместе с их юнитами.
* Казна ниже потолка 9999 (`0x586642` зажимает): у потолка перестают сходиться замеры денег.
* **Земля игрока — только столичное пятно, 25 клеток**: террейн Империи (`tile & 7 == 1`)
  лежит под столицей, вся остальная карта — нейтральная равнина. Это опора замера M7.

**Чем карты отличаются.**

| Карта | Что стоит | Зачем |
|---|---|---|
| M2 `f7-m2-growth` | столица + пустой нейтральный город 1 уровня в 2 клетках, казна 3000 | провести город 1→5 и снять цены 150/250/500/750 чистой казной |
| M3 `f7-m3-capture` | столица + нейтральный город **3 уровня с гарнизоном из трёх гоблинов** в 4 клетках | захват боем: +20 к броне защитникам и стирание гарнизона |
| M5 `f7-m5-regen` | столица + руина в 3 клетках, вокруг чистое поле | лестница лечения: поле +10, столица 35 %, руина 20 % |
| M7 `f7-m7-land` | столица + пустой нейтральный город 1 уровня в 2 клетках, казна 3000 | сложение квот роста земли: столица 10 + город 15…25 |

M2 и M7 отличаются не картой, а прогоном: на M2 город растят и смотрят казну, на M7 растят и
считают клетки своей земли. Собирать их одной картой нельзя — сохранения одного прогона
затирали бы сохранения другого.

**Почему гоблины.** `G000UU5017`: 50 HP и **броня 0** в `Gunits.dbf`. Ноль в базе — это и есть
смысл выбора: строка «Защита» в карточке защитника внутри города 3 уровня обязана показать
ровно прибавку города, без слагаемых. Трое таких — заведомо проходимый бой.

**Переживёт ли гарнизон загрузку.** Известные грабли — «дописанные в сохранение юниты
стираются» — про юнитов ИГРОКА: игра пересоздаёт игрока при выборе лорда. Гарнизон
нейтрального города — другой случай, и это не догадка: в 33 ванильных картах
`game-dev/Exports-archive` **106 нейтральных городов несут гарнизон**, из них **шесть** —
ровно наша конфигурация «уровень 3, три юнита» (например `'Greenskins'.sg S092FT000d`).
Мы пишем штатное состояние ванили, а не выдумку. Ранг 2 (доказано составом ванильных карт,
не дизассемблером); окончательная проверка — шаг 2 сценария S6: если гарнизон бы стирался,
город стоял бы пустым и захват прошёл бы без боя, что видно сразу. Обходной путь на этот
случай: посадить в город отряд-визитёр (`CMidStack` + поле `STACK` записи города) — так тоже
делает ваниль, — либо дописать гарнизон в сохранение после выбора лорда.

**Писатель руины** (`ruin_body`) сверен с ванилью байт в байт: каждая из 193 руин
`Exports-archive` пересобирается из своих же полей и сравнивается с исходным телом.
"""
from __future__ import annotations

import argparse
import functools
import sys
import tempfile
from dataclasses import dataclass, replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402
import make_c4_map as c4  # noqa: E402
import make_city_map as mc  # noqa: E402
import scenario_c4 as sc  # noqa: E402
from make_arena_map import set_bank  # noqa: E402

OUT_DIR = mc.MODDING / "game-dev" / "Exports"     # игра читает сценарии ТОЛЬКО отсюда
ARCHIVE = mc.MODDING / "game-dev" / "Exports-archive"

WHAT_RUIN = ".?AVCMidRuin@@"
RUIN_CLS = "RU"
UNIT_CLS = "UN"
RUIN_TILES = 3                       # пятно руины 3x3 — посчитано по плану 55 ванильных руин
EMPTY_BANK = "G0000:R0000:Y0000:E0000:W0000:B0000"

GOBLIN = "g000uu5017"                # 50 HP, броня 0, 5 опыта за убийство
LAND_TILES = len(c4.capital_tiles())  # 25: столько клеток своей земли на старте у всех карт


# ------------------------------------------------------------------ описание карты

@dataclass(frozen=True)
class Village:
    xy: tuple[int, int]
    tier: int
    garrison: tuple[str, ...] = ()
    name: str = "Город"
    gap: tuple[int, int] = (2, 4)     # допустимое расстояние от столицы, включительно


@dataclass(frozen=True)
class Ruin:
    xy: tuple[int, int]
    guards: tuple[str, ...] = (GOBLIN,)
    name: str = "Руина"
    gap: tuple[int, int] = (3, 3)


@dataclass(frozen=True)
class Spec:
    key: str
    name: str                         # имя в списке игры (и в ScenarioInfo.NAME, и в заголовке)
    file: str
    gold: int
    villages: tuple[Village, ...] = ()
    ruins: tuple[Ruin, ...] = ()
    hint: str = ""

    @property
    def bank(self) -> str:
        return "G%04d:R0000:Y0000:E0000:W0000:B0000" % self.gold


SPECS = (
    Spec("m2", "f7-m2-growth", "f7_m2_growth.sg", 3000,
         villages=(Village((12, 6), 1, name="Пустой город"),),
         hint="город 1 уровня в 2 клетках; растить его 1→5 и снимать казну после каждого шага"),
    Spec("m3", "f7-m3-capture", "f7_m3_capture.sg", 3000,
         villages=(Village((13, 6), 3, (GOBLIN,) * 3, "Гарнизонный город", (4, 4)),),
         hint="город 3 уровня с тремя гоблинами в 4 клетках; смотреть «Защиту» защитника до боя"),
    Spec("m5", "f7-m5-regen", "f7_m5_regen.sg", 1000,
         ruins=(Ruin((12, 7), name="Руина"),),
         hint="руина в 3 клетках, вокруг чистое поле; ночевать в поле, в столице и в руине"),
    Spec("m7", "f7-m7-land", "f7_m7_land.sg", 3000,
         villages=(Village((12, 6), 1, name="Соседний город"),),
         hint="город 1 уровня в 2 клетках; считать клетки своей земли по дням"),
)


def spec(key: str) -> Spec:
    for s in SPECS:
        if s.key == key:
            return s
    raise SystemExit("нет карты %s, есть %s" % (key, ", ".join(s.key for s in SPECS)))


# ----------------------------------------------------------------- запись руины

def ruin_body(rid: str, title: str, x: int, y: int, units: list[str],
              image: int = 0, cash: str = EMPTY_BANK, item: str = sg.NULL_ID,
              looter: str = sg.NULL_ID, aipriority: int = 3,
              cells: list[int] | None = None) -> bytes:
    """Тело `CMidRuin` по `sg.ksy: mid_ruin`. Порядок полей сверен с ванилью байт в байт
    (`check_ruin_writer`), поэтому здесь нет ни одного поля «на глаз»."""
    filled = len(units)
    units = list(units) + [sg.NULL_ID] * 6
    if cells is None:
        cells = [-1] * 6
        for i, cell in zip(range(filled), mc.FRONT_FIRST):
            cells[cell] = i
    return (sg.f_str("RUIN_ID", rid) + sg.f_str("TITLE", title) + sg.f_str("DESC", "")
            + sg.f_int("IMAGE", image) + sg.f_int("POS_X", x) + sg.f_int("POS_Y", y)
            + sg.f_str("CASH", cash) + sg.f_str("ITEM", item) + sg.f_str("LOOTER", looter)
            + sg.f_int("AIPRIORITY", aipriority)
            + sg.f_count(rid, 0)                     # список посетителей руины пуст
            + sg.f_str("GROUP_ID", rid)
            + b"".join(sg.f_str(f"UNIT_{k}", u) for k, u in enumerate(units[:6]))
            + b"".join(sg.f_int(f"POS_{k}", c) for k, c in enumerate(cells)))


_RUIN_HEAD = ("RUIN_ID:s TITLE:s DESC:s IMAGE:i POS_X:i POS_Y:i CASH:s ITEM:s LOOTER:s "
              "AIPRIORITY:i")


def parse_ruin(body: bytes) -> dict:
    c = sg.Cursor(body)
    out = c.fields(_RUIN_HEAD)
    out["VISITERS"] = [c.fields("RUIN_ID:s VISITER:s") for _ in range(c.count())]
    out.update(c.fields(sg._GROUP))
    if c.pos != len(body):
        raise SystemExit("тело руины разобрано не до конца: %d из %d" % (c.pos, len(body)))
    return out


def ruin_tiles(xy: tuple[int, int]) -> list[tuple[int, int]]:
    x0, y0 = xy
    return [(x0 + dx, y0 + dy) for dy in range(RUIN_TILES) for dx in range(RUIN_TILES)]


# ------------------------------------------------------- добавление объектов

@functools.lru_cache(maxsize=None)
def unit_hp(type_id: str) -> int:
    """`HIT_POINT` типа из `Gunits.dbf`; кешируется — иначе `.dbf` читается на каждый юнит."""
    return c4.hero_hp(type_id)


def add_units(scen: sg.Sg, types: tuple[str, ...]) -> tuple[sg.Sg, list[str]]:
    """Свежие `CMidUnit` первого уровня с полным здоровьем; возвращает их id по порядку."""
    ids: list[str] = []
    for type_id in types:
        uid = scen.next_ids(UNIT_CLS)[0]
        scen = scen.appended(sg.Record(sg.WHAT_UNIT, uid,
                                       sg.unit_body(uid, type_id, 1, unit_hp(type_id))))
        ids.append(uid)
    return scen, ids


def add_ruin(scen: sg.Sg, r: Ruin) -> tuple[sg.Sg, str]:
    scen, guards = add_units(scen, r.guards)
    rid = scen.next_ids(RUIN_CLS)[0]
    body = ruin_body(rid, r.name, r.xy[0], r.xy[1], guards)
    parse_ruin(body)                 # собранное обязано читаться нашим же курсором
    return scen.appended(sg.Record(WHAT_RUIN, rid, body)), rid


def add_objects(scen: sg.Sg, s: Spec, owner: str, subrace: str
                ) -> tuple[sg.Sg, list[tuple[int, int, str]]]:
    """Города и руины карты; возвращает ещё и клетки плана, которые они занимают."""
    plan: list[tuple[int, int, str]] = []
    for v in s.villages:
        scen, garrison = add_units(scen, v.garrison)
        scen, vid = mc.add_village(scen, owner, subrace, v.xy, v.tier, v.name, garrison)
        plan += [(x, y, vid) for x, y in mc.village_tiles(v.xy)]
    for r in s.ruins:
        scen, rid = add_ruin(scen, r)
        plan += [(x, y, rid) for x, y in ruin_tiles(r.xy)]
    return scen, plan


# ------------------------------------------------------------------------ сборка

SABOTAGE = ("maxunit", "mine", "tier", "garrison", "ruin", "far", "bank")
_SPARE_RUIN = Ruin((12, 20), name="Лишняя руина")
_FAR_VILLAGE, _FAR_RUIN = (40, 40), (40, 20)


def applicable(s: Spec, bad: str) -> bool:
    """Порча «уровень города» бессмысленна там, где города нет: подменять нечего, и такой
    случай надо назвать неприменимым вслух, а не записать в пропущенные сторожа."""
    return bool(s.villages) if bad == "tier" else True


def sabotaged(s: Spec, bad: str) -> Spec:
    """Испортить ровно одно обещание карты. Порча симметрична: где обещано «есть» — убираем,
    где обещано «нет» — добавляем, поэтому каждый случай применим к каждой из четырёх карт."""
    if bad not in SABOTAGE:
        raise SystemExit("неизвестная порча %s" % bad)
    if bad == "tier":
        return replace(s, villages=tuple(replace(v, tier=1 if v.tier != 1 else 5)
                                         for v in s.villages))
    if bad == "garrison":             # у руины «гарнизон» — её стражи, и портится так же
        return replace(s, villages=tuple(replace(v, garrison=() if v.garrison else (GOBLIN,))
                                         for v in s.villages),
                       ruins=tuple(replace(r, guards=() if r.guards else (GOBLIN,))
                                   for r in s.ruins))
    if bad == "ruin":
        return replace(s, ruins=() if s.ruins else (_SPARE_RUIN,))
    if bad == "far":
        return replace(s, villages=tuple(replace(v, xy=_FAR_VILLAGE) for v in s.villages),
                       ruins=tuple(replace(r, xy=_FAR_RUIN) for r in s.ruins))
    if bad == "bank":
        return replace(s, gold=s.gold + 1000)
    return s                          # maxunit и mine портятся внутри build


def build(template: Path, s: Spec, sabotage: str = "") -> tuple[sg.Sg, dict]:
    want = sabotaged(s, sabotage) if sabotage else s
    scen = sg.Sg.read(template)
    size = len(sg.tile_grid(scen))
    scen, changed = c4.set_terrain(scen, mc.wanted_tiles(size))
    scen = c4.move_capital(scen, *c4.CAPITAL_XY)

    hero = next(r for r in scen.find(sg.WHAT_STACK)
                if sg.parse_stack(r.body)["INSIDE"] != sg.NULL_ID)
    scen = c4.move_stack(scen, hero, *c4.CAPITAL_XY)
    scen, dropped = c4.drop_neutral_stacks(scen, keep=0)

    players = [sg.fields(r.body, sg.LAYOUT_PLAYER) for r in scen.find(sg.WHAT_PLAYER)]
    human = next(p for p in players if p["RACE_ID"] != sc.RACE_NEUTRAL)
    neutral = next(p for p in players if p["RACE_ID"] == sc.RACE_NEUTRAL)
    subrace = mc.neutral_subrace(scen, neutral["PLAYER_ID"], mc.NEUTRAL_SUBRACE)

    scen, plan = add_objects(scen, want, neutral["PLAYER_ID"], subrace)
    if sabotage == "mine":            # жила = кристалл: единственный источник дохода в игре
        scen, _ = c4.add_crystals(scen, [(0, 20, 20)])
    scen = set_bank(scen, human["PLAYER_ID"], want.bank)
    scen = mc.set_qty_cities(scen, len(want.villages))
    scen = mc.set_info_int(scen, "MAX_UNIT", 2 if sabotage == "maxunit" else mc.MAX_UNIT_WANTED)
    scen = c4.rebuild_plan(scen)      # столица, отряды и кристаллы
    scen = sg.add_plan_entries(scen, plan)
    scen = sg.set_scenario_name(scen, want.name)
    return scen, {"клеток перерисовано": changed, "отрядов удалено": len(dropped),
                  "городов": len(want.villages), "руин": len(want.ruins),
                  "казна": want.gold, "записей": len(scen.records)}


# ------------------------------------------------------------------- проверка

def _check_village(ch: mc.Checks, scen: sg.Sg, rec: sg.Record, v: Village,
                   neutral_ids: set[str], grid: list[list[int]], busy: set[tuple[int, int]],
                   types: dict[str, str]) -> None:
    f = mc.parse_village(rec.body, mc._has_p_o_el(scen))
    ch.is_true("владелец города нейтральный", f["OWNER"] in neutral_ids, f["OWNER"])
    ch.eq("уровень города %s" % (v.xy,), f["SIZE"], v.tier)
    units = [f["UNIT_%d" % k] for k in range(6) if f["UNIT_%d" % k] != sg.NULL_ID]
    ch.eq("юнитов в гарнизоне", len(units), len(v.garrison))
    ch.eq("занятых клеток строя", sum(f["POS_%d" % k] != -1 for k in range(6)), len(v.garrison))
    ch.eq("типы гарнизона", sorted(types.get(u.upper(), "?") for u in units),
          sorted(t.lower() for t in v.garrison))
    ch.eq("отряд-визитёр в городе", f["STACK"], sg.NULL_ID)
    ch.eq("бунт", f["RIOT_T"], 0)
    _check_reachable(ch, "город", mc.village_tiles((int(f["POS_X"]), int(f["POS_Y"]))),
                     v.gap, grid, busy)


def _check_ruin(ch: mc.Checks, rec: sg.Record, r: Ruin, grid: list[list[int]],
                busy: set[tuple[int, int]], types: dict[str, str]) -> None:
    f = parse_ruin(rec.body)
    units = [f["UNIT_%d" % k] for k in range(6) if f["UNIT_%d" % k] != sg.NULL_ID]
    ch.eq("стражей руины", len(units), len(r.guards))
    ch.eq("типы стражей", sorted(types.get(u.upper(), "?") for u in units),
          sorted(t.lower() for t in r.guards))
    ch.eq("руина не разграблена", f["LOOTER"], sg.NULL_ID)
    _check_reachable(ch, "руина", ruin_tiles((int(f["POS_X"]), int(f["POS_Y"]))),
                     r.gap, grid, busy)


def _check_reachable(ch: mc.Checks, what: str, tiles: list[tuple[int, int]],
                     want: tuple[int, int], grid: list[list[int]],
                     busy: set[tuple[int, int]]) -> None:
    """Объект обязан стоять на обещанном расстоянии от столицы, и путь до него — свободен."""
    cap = c4.capital_tiles()
    d = mc.gap(cap, tiles)
    ok = want[0] <= d <= want[1]
    ch.is_true("%s: расстояние от столицы %d" % (what, d), ok, "вне %s" % (want,))
    way = mc.corridor(cap, tiles) if ok else []
    ch.is_true("%s: коридор непуст" % what, bool(way))
    ch.eq("%s: клеток коридора занято объектом" % what, len(busy & set(way)), 0)
    ch.eq("%s: клеток коридора непроходимо" % what,
          sum(not sg.passable(grid[y][x]) for x, y in way), 0)


def verify(path: Path, s: Spec) -> None:
    """Прочитать записанный файл обратно и проверить числами всё, что карта обещает."""
    scen = sg.Sg.read(path)           # чтение само сверяет счётчик объектов и рамки записей
    ch = mc.Checks("%s: проверка чтением" % s.name)
    grid = sg.tile_grid(scen)
    want_tiles = mc.wanted_tiles(len(grid))
    ch.eq("клеток с чужим грунтом",
          sum(grid[y][x] != t[0] for (x, y), t in want_tiles.items()), 0)
    ch.eq("клеток своей земли (террейн Империи)",
          sum(1 for row in grid for t in row if t & 7 == 1), LAND_TILES)

    info = mc.info_fields(scen)
    ch.eq("MAX_UNIT (потолок уровня построек)", info["MAX_UNIT"], mc.MAX_UNIT_WANTED)
    ch.eq("имя сценария в ScenarioInfo", sg.scenario_name(scen), s.name)
    ch.eq("имя сценария в заголовке файла",
          sg.header_name(scen), s.name)

    players = [sg.fields(r.body, sg.LAYOUT_PLAYER) for r in scen.find(sg.WHAT_PLAYER)]
    human = [p for p in players if p["RACE_ID"] != sc.RACE_NEUTRAL]
    neutral_ids = {p["PLAYER_ID"] for p in players if p["RACE_ID"] == sc.RACE_NEUTRAL}
    ch.eq("играбельных игроков", len(human), 1)
    ch.eq("казна игрока", human[0]["BANK"], s.bank)
    ch.eq("жил (кристаллов) на карте", len(scen.find(c4.WHAT_CRYSTAL)), 0)

    stacks = [sg.parse_stack(r.body) for r in scen.find(sg.WHAT_STACK)]
    own = [st for st in stacks if st["OWNER"] == human[0]["PLAYER_ID"]]
    ch.eq("своих отрядов", len(own), 1)
    ch.eq("отрядов противника на карте", len(stacks) - len(own), 0)

    villages, ruins = scen.find(mc.WHAT_VILLAGE), scen.find(WHAT_RUIN)
    ch.eq("городов на карте", len(villages), len(s.villages))
    ch.eq("руин на карте", len(ruins), len(s.ruins))
    ch.eq("QTY_CITIES", info["QTY_CITIES"], len(s.villages))

    _, plan = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    busy = {(x, y) for x, y, _ in plan}
    types = {r.obj_id.upper(): sg.fields(r.body, sg.LAYOUT_UNIT)["TYPE"].lower()
             for r in scen.find(sg.WHAT_UNIT)}
    for rec, v in zip(villages, s.villages):
        _check_village(ch, scen, rec, v, neutral_ids, grid, busy, types)
        ch.eq("клеток города в плане не на месте", _plan_shape(plan, rec.obj_id,
              mc.village_tiles(v.xy) if _at(scen, rec, v.xy) else []), 0)
    for rec, r in zip(ruins, s.ruins):
        _check_ruin(ch, rec, r, grid, busy, types)
        ch.eq("клеток руины в плане не на месте", _plan_shape(plan, rec.obj_id,
              ruin_tiles(r.xy) if _at(scen, rec, r.xy) else []), 0)

    ids = {r.obj_id for r in scen.records}
    ch.eq("висячих ссылок в плане", len({e for _, _, e in plan} - ids), 0)
    ch.eq("висячих ссылок в отрядах",
          sum(1 for st in stacks
              for ref in [st["UNIT_%d" % k] for k in range(6)] + list(st["ITEMS"])
              if ref != sg.NULL_ID and ref not in ids), 0)
    ch.eq("висячих ссылок в гарнизонах и руинах",
          sum(1 for rec in villages + ruins
              for ref in _group_refs(scen, rec) if ref != sg.NULL_ID and ref not in ids), 0)
    ch.done()


def _at(scen: sg.Sg, rec: sg.Record, xy: tuple[int, int]) -> bool:
    """Стоит ли объект там, где обещано (иначе форму пятна в плане сверять не с чем)."""
    f = sg.fields(rec.body, _head_for(rec))
    return (int(f["POS_X"]), int(f["POS_Y"])) == xy


def _head_for(rec: sg.Record) -> str:
    return _RUIN_HEAD.split(" CASH:s")[0] if rec.what == WHAT_RUIN else sg.LAYOUT_CAPITAL


def _plan_shape(plan: list[tuple[int, int, str]], obj_id: str,
                want: list[tuple[int, int]]) -> int:
    """Сколько клеток пятна в плане разошлось с ожидаемым (0 — совпало полностью)."""
    got = {(x, y) for x, y, e in plan if e == obj_id}
    return len(got ^ set(want)) if want else max(len(got), 1)


def _group_refs(scen: sg.Sg, rec: sg.Record) -> list[str]:
    f = (parse_ruin(rec.body) if rec.what == WHAT_RUIN
         else mc.parse_village(rec.body, mc._has_p_o_el(scen)))
    return [f["UNIT_%d" % k] for k in range(6)]


# ------------------------------------------------ писатель руины против ванили

def check_ruin_writer(archive: Path = ARCHIVE) -> None:
    """Каждая ванильная руина пересобирается `ruin_body` из своих же полей и сравнивается с
    исходным телом БАЙТ В БАЙТ. Ноль сравнений — тоже провал: пустой отчёт ничего не доказывает.
    """
    if not archive.is_dir():
        raise SystemExit("нет каталога ванильных карт %s — сверять писателя нечем" % archive)
    same = diff = 0
    for f in sorted(archive.glob("*.sg")):
        for rec in sg.Sg.read(f).find(WHAT_RUIN):
            v = parse_ruin(rec.body)
            units = [v["UNIT_%d" % k] for k in range(6)]
            body = ruin_body(rec.obj_id, v["TITLE"], int(v["POS_X"]), int(v["POS_Y"]),
                             [u for u in units if u != sg.NULL_ID], int(v["IMAGE"]),
                             v["CASH"], v["ITEM"], v["LOOTER"], int(v["AIPRIORITY"]),
                             cells=[v["POS_%d" % k] for k in range(6)])
            same += body == rec.body
            diff += body != rec.body
    print("писатель руины против ванили: воспроизведено байт в байт %d, разошлось %d"
          % (same, diff))
    if not same + diff:
        raise SystemExit("в ванильных картах не найдено ни одной руины — сравнений ноль")
    if diff:
        raise SystemExit("раскладка руины неверна: разошлось тел %d" % diff)


def check_garrison_survives(archive: Path = ARCHIVE) -> None:
    """Гарнизон нейтрального города — штатное состояние ванили, а не наша выдумка.

    Считаем по 33 ванильным картам: сколько нейтральных городов несут гарнизон и сколько из
    них — ровно конфигурация M3 «уровень 3, три юнита». Ноль второго числа означал бы, что мы
    первыми пишем такое состояние, и тогда о рисках надо говорить вслух, а не молчать.
    """
    with_garrison = like_m3 = total = 0
    for f in sorted(archive.glob("*.sg")):
        scen = sg.Sg.read(f)
        el = mc._has_p_o_el(scen)
        for rec in scen.find(mc.WHAT_VILLAGE):
            v = mc.parse_village(rec.body, el)
            n = sum(v["UNIT_%d" % k] != sg.NULL_ID for k in range(6))
            total += 1
            with_garrison += n > 0
            like_m3 += n == 3 and v["SIZE"] == 3
    print("ванильные нейтральные города: всего %d, с гарнизоном %d, «уровень 3 + три юнита» %d"
          % (total, with_garrison, like_m3))
    if not total:
        raise SystemExit("ванильных городов не найдено — сравнений ноль")
    if not like_m3:
        raise SystemExit("конфигурация M3 в ванили не встречается — гарнизон под вопросом")


# ------------------------------------------------------------------- самопроверка

def selftest(template: Path) -> int:
    """Каждая карта собирается начисто и семь раз с одной испорченной величиной; порченое
    читается тем же `verify`, что и чистое. Игра не запускается, игровые каталоги не трогаются.
    """
    check_ruin_writer()
    check_garrison_survives()
    print()
    caught = missed = clean_ok = 0
    lines: list[str] = []
    with tempfile.TemporaryDirectory(prefix="d2f7") as tmp:
        out = Path(tmp) / "probe.sg"
        for s in SPECS:
            scen, _ = build(template, s)
            scen.write(out)
            try:
                verify(out, s)
                clean_ok += 1
                lines.append("%-14s чистая сборка проходит" % s.name)
            except SystemExit as e:
                lines.append("%-14s чистая сборка ПАДАЕТ: %s" % (s.name, e))
            for bad in SABOTAGE:
                if not applicable(s, bad):
                    lines.append("%-14s порча %-9s неприменима (города на карте нет)"
                                 % (s.name, bad))
                    continue
                scen, _ = build(template, s, sabotage=bad)
                scen.write(out)
                try:
                    verify(out, s)
                    missed += 1
                    lines.append("%-14s порча %-9s ПРОПУЩЕНА" % (s.name, bad))
                except SystemExit as e:
                    caught += 1
                    lines.append("%-14s порча %-9s ловится: %s" % (s.name, bad, e))
    print()
    for line in lines:
        print(line)
    print("selftest: сторожей сработало %d, пропущено %d из %d; чистых сборок прошло %d из %d"
          % (caught, missed, caught + missed, clean_ok, len(SPECS)))
    return 0 if missed == 0 and clean_ok == len(SPECS) else 1


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--template", type=Path, default=c4.TEMPLATE)
    p.add_argument("--out-dir", type=Path, default=OUT_DIR)
    p.add_argument("--only", default="", help="ключ карты: m2, m3, m5, m7")
    p.add_argument("--selftest", action="store_true", help="проверить сторожей подсадной ошибкой")
    a = p.parse_args(argv)
    if not a.template.exists():
        raise SystemExit("нет шаблона %s" % a.template)
    if a.selftest:
        return selftest(a.template)
    a.out_dir.mkdir(parents=True, exist_ok=True)
    for s in (SPECS if not a.only else (spec(a.only),)):
        scen, stats = build(a.template, s)
        scen.write(a.out_dir / s.file)
        print("%s: %s" % (a.out_dir / s.file, ", ".join("%s %s" % kv for kv in stats.items())))
        verify(a.out_dir / s.file, s)
        print("   в списке игры «%s» — %s" % (s.name, s.hint))
    return 0


if __name__ == "__main__":
    sys.exit(main())
