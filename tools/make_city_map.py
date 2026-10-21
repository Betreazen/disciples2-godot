#!/usr/bin/env python3
"""Карта `city-hotkeys`: столица Империи, рядом пустой нейтральный город, врагов нет.

    python tools/make_city_map.py
    python tools/make_city_map.py --selftest   # сторожа проверки подсадной ошибкой

**Зачем.** У обычного города (не столицы) одно окно — окно отрядов, и в нём есть кнопка
роста города. Ни вида кнопок, ни клавиш этого окна мы не знаем: файлы интерфейса уже соврали
один раз (`Interf.dlg` дал «`G` — Отстроить город», а у города такого окна как отдельного нет).
Единственный способ узнать — войти в город живьём и назвать хоткеи вслух. Карта нужна ровно
для этого: захватить город за один ход и спокойно жать кнопки, ничем не рискуя.

**Что на карте.** Столица в (5,5) — пятно 5x5. Город в (12,6) — пятно 4x4, владелец
нейтральный, гарнизон пуст (в ванили таких 80 из 178 нейтральных городов, так что это
штатное состояние, а не выдумка). Между ними две свободные колонки равнины: расстояние по
Чебышёву — 3 клетки, то есть три шага из двадцати очков. Ни одного нейтрального отряда и ни
одного второго игрока с войсками: `drop_neutral_stacks(keep=0)` сносит все восемнадцать
отрядов шаблона вместе с их юнитами.

**Казна 5000 золота.** Рост города стоит `GcityInf[текущий размер].GROWTH` = 150, 250, 500,
750 (`cities.md` §1.2, ранг 1) — все четыре уровня это 1650. Потолок казны 9999 (`0x586642`
зажимает), и упираться в него нельзя: у потолка перестают сходиться замеры дохода. 5000 —
между этими двумя числами с запасом.

**Генератора нейтральных городов у нас не было** — разведка 09.09.2026 это установила. Он
здесь: `village_body` собирает запись `CMidVillage` по раскладке `tools/ksy/sg.ksy`
(`mid_village`, стр. 914-943). Раскладка не взята на веру, а сверена дочитыванием тел живых
городов до последнего байта: `Tutorial.sg` (MidFile 35) и `Mercantile.sg` (D2EESFISIG) —
курсор доходит ровно до `len(body)`. Единственное различие форматов — поле `P_O_EL`,
которого в `MidFile` нет; шаблон у нас `D2EESFISIG`, значит поле пишется.

**Чего здесь нет и почему.** Постройки и состав отряда сценарием не задаются вовсе: игра
пересоздаёт игрока при выборе лорда и обнуляет и здания, и солдат (`fidelity-f5.md` §5 п. 8).
Сценарием задаются только карта, города, казна и объекты — этой задаче ровно этого и хватает,
патчить сохранение не требуется. Маршрута для драйвера тоже нет: карту играет человек.

Размер карты — 48x48, как у всех прежних генераторов: это размер шаблона
`game-dev/Exports/test_fast_battle.sg`, а меньший потребовал бы пересборки 72 блоков карты
ради нуля пользы.
"""
from __future__ import annotations

import argparse
import sys
import tempfile
from dataclasses import replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402
import make_c4_map as c4  # noqa: E402
import scenario_c4 as sc  # noqa: E402
from make_arena_map import set_bank  # noqa: E402  (правка BANK в записи игрока)

MODDING = Path(__file__).resolve().parents[2] / "disciples-modding"
OUT = MODDING / "game-dev" / "scenarios" / "city_hotkeys.sg"
NAME = "city-hotkeys"

WHAT_VILLAGE = ".?AVCMidVillage@@"
VILLAGE_CLS = "FT"                   # тот же класс id, что у столицы (S143FT0000)
VILLAGE_TILES = 4                    # пятно города 4x4 — посчитано по плану 194 ванильных
                                     # городов: у каждого ровно 16 клеток

VILLAGE_XY = (12, 6)                 # левый верхний угол пятна; столица занимает (5..9, 5..9)
VILLAGE_TIER = 1                     # первый уровень: расти можно четыре раза подряд
VILLAGE_NAME = "Тестовый город"
NEUTRAL_SUBRACE = 5                  # Lsubrace: подраса нейтральных городов ванили (29 из 178
                                     # нейтральных городов уровня 1 — именно она)
GAP_MIN, GAP_MAX = 2, 4              # требование задачи: город в 2-4 клетках по равнине

BANK = "G5000:R0000:Y0000:E0000:W0000:B0000"
MAX_CITY_WANTED = 5                  # ScenarioInfo.MAX_CITY — потолок уровня города в сценарии
# `MAX_UNIT` записи `CScenarioInfo` (`unitMaxTier`, смещение 100) — вопреки имени это потолок
# уровня ПОСТРОЙКИ: движок сравнивает `Gbuild.LEVEL` с ним и отвечает `ExceedsMaxLevel`
# (`cities.md`, ранг 1). В шаблоне стоит 2, из-за чего Белая башня (`LEVEL 4`) не строилась.
# Максимальный `LEVEL` в `Gbuild.dbf` — 5, значит при пятёрке не откажет ни одному зданию.
MAX_UNIT_WANTED = 5
FRONT_FIRST = (0, 2, 4, 1, 3, 5)     # порядок заполнения клеток строя: сперва передний ряд

_INFO_TO_QTY = "INFO_ID:s CAMPAIGN:s SOURCE_M:b"


# ------------------------------------------------------------------ геометрия

def village_tiles(xy: tuple[int, int] = VILLAGE_XY) -> list[tuple[int, int]]:
    x0, y0 = xy
    return [(x0 + dx, y0 + dy) for dy in range(VILLAGE_TILES) for dx in range(VILLAGE_TILES)]


def gap(a: list[tuple[int, int]], b: list[tuple[int, int]]) -> int:
    """Чебышёв между ближайшими клетками двух пятен: столько шагов идти от края до края."""
    return min(max(abs(ax - bx), abs(ay - by)) for ax, ay in a for bx, by in b)


def corridor(cap: list[tuple[int, int]], vil: list[tuple[int, int]]) -> list[tuple[int, int]]:
    """Клетки строго между столицей и городом. Город стоит ВОСТОЧНЕЕ — на этом держится
    расчёт, и если раскладку поменяют, сборка обязана упасть здесь, а не в игре."""
    cx1, vx0 = max(x for x, _ in cap), min(x for x, _ in vil)
    if vx0 <= cx1:
        raise SystemExit("город %s не восточнее столицы — коридор считать нечем" % (VILLAGE_XY,))
    ys = sorted({y for _, y in cap} & {y for _, y in vil})
    if not ys:
        raise SystemExit("столица и город не перекрываются по Y — прямого коридора нет")
    return [(x, y) for x in range(cx1 + 1, vx0) for y in ys]


def wanted_tiles(size: int) -> dict[tuple[int, int], bytes]:
    """Вся карта — равнина, поверх неё пятно столицы. Полос леса, воды и гор здесь нет:
    пользователю нужно дойти три клетки, а не мерить стоимость шага."""
    tiles = {(x, y): c4.TILE[0] for y in range(size) for x in range(size)}
    for x, y in c4.capital_tiles():
        tiles[(x, y)] = c4.TILE_CAPITAL
    return tiles


# --------------------------------------------------------- запись города

def village_body(vid: str, name: str, owner: str, subrace: str, x: int, y: int,
                 tier: int, units: list[str] | None = None, has_p_o_el: bool = True,
                 protect_b: str = sg.NULL_ID, regen_b: int = 0, riot_t: int = 0) -> bytes:
    """Тело `CMidVillage` по `sg.ksy: mid_village`. Гарнизон по умолчанию пуст (шесть NULL и
    шесть -1), инвентарь пуст, модификатор защиты и регенерация — нули, как у ванильных.
    `protect_b` (модификатор защиты), `regen_b` (регенерация), `riot_t` (ходов бунта) — поля
    `PROTECT_B`/`REGEN_B`/`RIOT_T`; умолчания дают прежнее тело байт в байт."""
    filled = len(units or [])
    units = (units or []) + [sg.NULL_ID] * 6
    # POS_k — индекс юнита в группе, занимающего клетку строя k (сверено на `Tutorial.sg`:
    # три юнита стоят как POS_1=2, POS_3=0, POS_5=1). Заполняем сначала передний ряд
    # (клетки 0, 2, 4), потом задний (1, 3, 5) — так стоят гарнизоны ванильных городов
    # ('Greenskins'.sg S092FT000d: POS_0=0, POS_2=2, POS_4=1), и так все защитники достают
    # до атакующего, а не отсиживаются во втором ряду.
    cells = [-1] * 6
    for i, cell in zip(range(filled), FRONT_FIRST):
        cells[cell] = i
    body = (sg.f_str("CITY_ID", vid) + sg.f_str("NAME_TXT", name) + sg.f_str("DESC_TXT", "")
            + sg.f_str("OWNER", owner) + sg.f_str("SUBRACE", subrace)
            + sg.f_str("STACK", sg.NULL_ID)
            + sg.f_int("POS_X", x) + sg.f_int("POS_Y", y)
            + sg.f_str("GROUP_ID", vid)
            + b"".join(sg.f_str(f"UNIT_{k}", u) for k, u in enumerate(units[:6]))
            + b"".join(sg.f_int(f"POS_{k}", c) for k, c in enumerate(cells))
            + sg.f_count(vid, 0)
            + sg.f_int("AIPRIORITY", 3)
            + sg.f_str("PROTECT_B", protect_b)
            + sg.f_int("REGEN_B", regen_b) + sg.f_int("MORALE", 0) + sg.f_int("GROWTH_T", 0)
            + sg.f_int("SIZE", tier)
            + sg.f_bool("P_O_UN", 0) + sg.f_bool("P_O_HE", 0)
            + sg.f_bool("P_O_HU", 0) + sg.f_bool("P_O_DW", 0))
    if has_p_o_el:
        body += sg.f_bool("P_O_EL", 0)
    return body + sg.f_int("RIOT_T", riot_t)


def parse_village(body: bytes, has_p_o_el: bool = True) -> dict:
    c = sg.Cursor(body)
    out = c.fields(sg.LAYOUT_CAPITAL)
    out.update(c.fields(sg._GROUP))
    out["ITEMS"] = [c.str("ITEM_ID") for _ in range(c.count())]
    tail = "AIPRIORITY:i PROTECT_B:s REGEN_B:i MORALE:i GROWTH_T:i SIZE:i P_O_UN:b P_O_HE:b P_O_HU:b P_O_DW:b"
    if has_p_o_el:
        tail += " P_O_EL:b"
    out.update(c.fields(tail + " RIOT_T:i"))
    if c.pos != len(body):
        raise SystemExit("тело города разобрано не до конца: %d из %d" % (c.pos, len(body)))
    return out


def add_village(scen: sg.Sg, owner: str, subrace: str, xy: tuple[int, int],
                tier: int, name: str, units: list[str] | None = None) -> tuple[sg.Sg, str]:
    vid = scen.next_ids(VILLAGE_CLS)[0]
    body = village_body(vid, name, owner, subrace, xy[0], xy[1], tier, units,
                        has_p_o_el=_has_p_o_el(scen))
    parse_village(body, _has_p_o_el(scen))          # собранное обязано читаться нашим же курсором
    return scen.appended(sg.Record(WHAT_VILLAGE, vid, body)), vid


def _has_p_o_el(scen: sg.Sg) -> bool:
    """`P_O_EL` есть только в формате Rise of the Elves (`sg.ksy: mid_village`)."""
    return scen.header[:10].split(b"\x00")[0] == b"D2EESFISIG"


def neutral_subrace(scen: sg.Sg, player_id: str, value: int) -> str:
    for r in scen.find(sg.WHAT_SUBRACE):
        f = sg.fields(r.body, sg.LAYOUT_SUBRACE)
        if f["PLAYER_ID"] == player_id and f["SUBRACE"] == value:
            return f["SUBRACE_ID"]
    raise SystemExit("у нейтрала %s нет записи подрасы %d" % (player_id, value))


def info_layout(scen: sg.Sg) -> str:
    """Раскладка тела `CScenarioInfo` целиком: одна на чтение полей и на правку любого из них."""
    lay = (_INFO_TO_QTY + " QTY_CITIES:i NAME:s DESC:s BRIEFING:s DEBUNKW:s DEBUNKW2:s "
           "DEBUNKW3:s DEBUNKW4:s DEBUNKW5:s DEBUNKL:s BRIEFLONG1:s BRIEFLONG2:s BRIEFLONG3:s "
           "BRIEFLONG4:s BRIEFLONG5:s O:b CUR_TURN:i MAX_UNIT:i MAX_SPELL:i MAX_LEADER:i "
           "MAX_CITY:i MAP_SIZE:i")
    if not _has_p_o_el(scen):        # в MidFile 34 четырёх запасных DEBUNKW нет
        lay = lay.replace(" DEBUNKW2:s DEBUNKW3:s DEBUNKW4:s DEBUNKW5:s", "")
    return lay


def set_info_int(scen: sg.Sg, field: str, value: int) -> sg.Sg:
    """Переписать целое поле `CScenarioInfo` на месте: курсор доходит до него по раскладке,
    само поле пересобирается, хвост тела копируется байт в байт."""
    lay = info_layout(scen)
    head, mark = lay.split(" %s:i" % field)[0], " %s:i" % field
    if mark not in lay:
        raise SystemExit("в раскладке ScenarioInfo нет целого поля %s" % field)
    rec = scen.one(sg.WHAT_INFO)
    c = sg.Cursor(rec.body)
    c.fields(head)
    start = c.pos
    c.int(field)
    body = rec.body[:start] + sg.f_int(field, value) + rec.body[c.pos:]
    return scen.with_record(rec, replace(rec, body=body))


def set_qty_cities(scen: sg.Sg, n: int) -> sg.Sg:
    """`ScenarioInfo.QTY_CITIES` — число городов без столицы (сверено: Tutorial 3, шаблон 0)."""
    return set_info_int(scen, "QTY_CITIES", n)


def info_fields(scen: sg.Sg) -> dict:
    return sg.fields(scen.one(sg.WHAT_INFO).body, info_layout(scen))


# ------------------------------------------------------------------------ сборка

SABOTAGE = ("garrison", "owner", "far", "bank", "hostile", "maxunit")


def build(template: Path, name: str = NAME, sabotage: str = "") -> tuple[sg.Sg, dict]:
    """Собрать карту. `sabotage` — подсадная порча ровно одного обещания, для `--selftest`."""
    if sabotage and sabotage not in SABOTAGE:
        raise SystemExit("неизвестная порча %s" % sabotage)
    scen = sg.Sg.read(template)
    size = len(sg.tile_grid(scen))
    scen, changed = c4.set_terrain(scen, wanted_tiles(size))
    scen = c4.move_capital(scen, *c4.CAPITAL_XY)

    hero = next(r for r in scen.find(sg.WHAT_STACK)
                if sg.parse_stack(r.body)["INSIDE"] != sg.NULL_ID)
    scen = c4.move_stack(scen, hero, *c4.CAPITAL_XY)
    scen, dropped = c4.drop_neutral_stacks(scen, keep=1 if sabotage == "hostile" else 0)

    players = [sg.fields(r.body, sg.LAYOUT_PLAYER) for r in scen.find(sg.WHAT_PLAYER)]
    human = next(p for p in players if p["RACE_ID"] != sc.RACE_NEUTRAL)
    neutral = next(p for p in players if p["RACE_ID"] == sc.RACE_NEUTRAL)

    owner = human["PLAYER_ID"] if sabotage == "owner" else neutral["PLAYER_ID"]
    xy = (40, 40) if sabotage == "far" else VILLAGE_XY
    garrison = [sg.parse_stack(hero.body)["LEADER_ID"]] if sabotage == "garrison" else []
    scen, vid = add_village(scen, owner,
                            neutral_subrace(scen, neutral["PLAYER_ID"], NEUTRAL_SUBRACE),
                            xy, VILLAGE_TIER, VILLAGE_NAME, garrison)

    scen = set_bank(scen, human["PLAYER_ID"],
                       "G1000:R0000:Y0000:E0000:W0000:B0000" if sabotage == "bank" else BANK)
    scen = set_qty_cities(scen, len(scen.find(WHAT_VILLAGE)))
    scen = set_info_int(scen, "MAX_UNIT", 2 if sabotage == "maxunit" else MAX_UNIT_WANTED)
    scen = c4.rebuild_plan(scen)
    scen = sg.add_plan_entries(scen, [(x, y, vid) for x, y in village_tiles(xy)])
    scen = sg.set_scenario_name(scen, name)
    return scen, {"клеток перерисовано": changed, "отрядов удалено": len(dropped),
                  "городов добавлено": 1, "казна": BANK.split(":")[0],
                  "записей": len(scen.records)}


# ------------------------------------------------------------------- проверка

class Checks:
    """Счётчик сравнений: пары «сошлось / разошлось», падение и на расхождении, и на нуле.

    Ноль сравнений — не «всё хорошо», а «ничего не проверено»: пустой отчёт ничего не
    доказывает, и он тоже обязан ронять прогон.
    """

    def __init__(self, title: str) -> None:
        self.title, self.good, self.bad = title, 0, []

    def eq(self, what: str, got, want) -> None:
        self.is_true(what, got == want, "%r, ожидалось %r" % (got, want))

    def is_true(self, what: str, cond: bool, detail: str = "") -> None:
        if cond:
            self.good += 1
        else:
            self.bad.append("%s: %s" % (what, detail or "не выполнено"))

    def done(self) -> None:
        total = self.good + len(self.bad)
        print("%s: сошлось %d, разошлось %d (сравнений %d)"
              % (self.title, self.good, len(self.bad), total))
        for line in self.bad:
            print("  РАЗОШЛОСЬ %s" % line)
        if not total:
            raise SystemExit("%s: сравнений ноль — проверка ничего не доказала" % self.title)
        if self.bad:
            raise SystemExit("%s: расхождений %d" % (self.title, len(self.bad)))


def verify(path: Path) -> None:
    """Прочитать записанный файл обратно и проверить числами всё, что генератор обещал."""
    scen = sg.Sg.read(path)          # чтение само сверяет счётчик объектов и рамки записей
    ch = Checks("проверка чтением")
    grid = sg.tile_grid(scen)
    want = wanted_tiles(len(grid))
    ch.eq("клеток с чужим грунтом", sum(grid[y][x] != t[0] for (x, y), t in want.items()), 0)

    players = [sg.fields(r.body, sg.LAYOUT_PLAYER) for r in scen.find(sg.WHAT_PLAYER)]
    human = [p for p in players if p["RACE_ID"] != sc.RACE_NEUTRAL]
    ch.eq("играбельных игроков", len(human), 1)
    neutral_ids = {p["PLAYER_ID"] for p in players if p["RACE_ID"] == sc.RACE_NEUTRAL}
    ch.eq("казна игрока", human[0]["BANK"], BANK)

    stacks = [sg.parse_stack(r.body) for r in scen.find(sg.WHAT_STACK)]
    own = [s for s in stacks if s["OWNER"] == human[0]["PLAYER_ID"]]
    ch.eq("своих отрядов", len(own), 1)
    ch.eq("враждебных отрядов", len(stacks) - len(own), 0)

    villages = scen.find(WHAT_VILLAGE)
    ch.eq("городов на карте", len(villages), 1)
    ch.eq("QTY_CITIES", info_fields(scen)["QTY_CITIES"], len(villages))
    ch.eq("MAX_CITY", info_fields(scen)["MAX_CITY"], MAX_CITY_WANTED)
    ch.eq("MAX_UNIT (потолок уровня построек)", info_fields(scen)["MAX_UNIT"], MAX_UNIT_WANTED)
    ch.eq("имя сценария", sg.scenario_name(scen), NAME)
    if villages:
        v = parse_village(villages[0].body, _has_p_o_el(scen))
        ch.is_true("владелец города нейтральный", v["OWNER"] in neutral_ids, v["OWNER"])
        ch.eq("уровень города", v["SIZE"], VILLAGE_TIER)
        ch.eq("юнитов в гарнизоне",
              sum(v[f"UNIT_{k}"] != sg.NULL_ID for k in range(6)), 0)
        ch.eq("занятых клеток строя", sum(v[f"POS_{k}"] != -1 for k in range(6)), 0)
        ch.eq("отряд-визитёр в городе", v["STACK"], sg.NULL_ID)
        ch.eq("бунт", v["RIOT_T"], 0)

        vil = village_tiles((int(v["POS_X"]), int(v["POS_Y"])))
        cap = c4.capital_tiles()
        d = gap(cap, vil)
        ch.is_true("расстояние столица-город %d клеток" % d, GAP_MIN <= d <= GAP_MAX,
                   "вне [%d, %d]" % (GAP_MIN, GAP_MAX))
        _, plan = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
        busy = {(x, y) for x, y, _ in plan}
        way = corridor(cap, vil) if GAP_MIN <= d <= GAP_MAX else []
        ch.is_true("коридор между ними непуст", bool(way))
        ch.eq("клеток коридора занято объектом", len(busy & set(way)), 0)
        ch.eq("клеток коридора непроходимо",
              sum(not sg.passable(grid[y][x]) for x, y in way), 0)
        ch.eq("клеток города в плане",
              sum(1 for x, y, e in plan if e == villages[0].obj_id), len(vil))
        ch.eq("клетки города в плане не на месте",
              len({(x, y) for x, y, e in plan if e == villages[0].obj_id} ^ set(vil)), 0)

    ids = {r.obj_id for r in scen.records}
    _, plan = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    ch.eq("висячих ссылок в плане", len({e for _, _, e in plan} - ids), 0)
    ch.eq("висячих ссылок в отрядах",
          sum(1 for s in stacks for ref in [s[f"UNIT_{k}"] for k in range(6)] + list(s["ITEMS"])
              if ref != sg.NULL_ID and ref not in ids), 0)
    ch.done()


ARCHIVE = MODDING / "game-dev" / "Exports-archive"


def check_writer_vs_vanilla(archive: Path = ARCHIVE) -> None:
    """Доказать писателя записи города ванильными байтами, а не своим же курсором.

    Каждый нейтральный город ванильных карт пересобирается `village_body` из полей, прочитанных
    у него самого, и сравнивается с исходным телом БАЙТ В БАЙТ. Совпасть могут только те, у
    которых все прочие поля равны нашим умолчаниям (пустой гарнизон и инвентарь, `AIPRIORITY 3`,
    нулевые `REGEN_B`/`MORALE`/`GROWTH_T`/`RIOT_T`, пустой `PROTECT_B`) — таких мало, но ноль
    означал бы, что раскладка сочинена. Заодно проверяется отрицательная сторона: без `P_O_EL`
    в формате Rise of the Elves не должно совпасть ни одно тело.
    """
    if not archive.is_dir():
        raise SystemExit("нет каталога ванильных карт %s — сверять писателя нечем" % archive)
    same = diff = wrong_fmt = 0
    for f in sorted(archive.glob("*.sg")):
        scen = sg.Sg.read(f)
        el = _has_p_o_el(scen)
        for r in scen.find(WHAT_VILLAGE):
            v = parse_village(r.body, el)
            args = (r.obj_id, v["NAME_TXT"], v["OWNER"], v["SUBRACE"],
                    int(v["POS_X"]), int(v["POS_Y"]), int(v["SIZE"]), [])
            same += village_body(*args, has_p_o_el=el) == r.body
            diff += village_body(*args, has_p_o_el=el) != r.body
            wrong_fmt += el and village_body(*args, has_p_o_el=False) == r.body
    print("писатель против ванили: воспроизведено байт в байт %d, разошлось %d "
          "(из них по составу полей, а не по раскладке); без P_O_EL совпало %d"
          % (same, diff, wrong_fmt))
    if not same + diff:
        raise SystemExit("в ванильных картах не найдено ни одного города — сравнений ноль")
    if not same:
        raise SystemExit("ни одно ванильное тело города не воспроизведено — раскладка неверна")
    if wrong_fmt:
        raise SystemExit("тело совпало и БЕЗ P_O_EL — различие форматов проверено неверно")


def selftest(template: Path) -> int:
    """Сторожа проверяются подсадной ошибкой: «упало бы» надо доказать, а не пообещать.

    Каждый случай проходит НАСТОЯЩУЮ сборку с одной испорченной величиной, пишется в файл и
    читается обратно тем же `verify`. Ничего не пишет в игровые каталоги и не запускает игру.
    """
    cases: list[tuple[str, str | None]] = []
    check_writer_vs_vanilla()
    with tempfile.TemporaryDirectory(prefix="d2city") as tmp:
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
        print("порча %-9s %s" % (bad, ("ловится: " + msg) if msg else "ПРОПУЩЕНА"))
    print("чистая сборка       %s" % ("проходит" if clean is None else "ПАДАЕТ: " + clean))
    print("selftest: сторожей сработало %d, пропущено %d из %d; чистых сборок прошло %d из 1"
          % (caught, len(cases) - caught, len(cases), int(clean is None)))
    return 0 if caught == len(cases) and clean is None else 1


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--template", type=Path, default=c4.TEMPLATE)
    p.add_argument("--out", type=Path, default=OUT)
    p.add_argument("--name", default=NAME)
    p.add_argument("--selftest", action="store_true", help="проверить сторожей подсадной ошибкой")
    a = p.parse_args(argv)
    if not a.template.exists():
        raise SystemExit("нет шаблона %s" % a.template)
    if a.selftest:
        return selftest(a.template)
    scen, stats = build(a.template, a.name)
    a.out.parent.mkdir(parents=True, exist_ok=True)
    scen.write(a.out)
    print("%s: %s" % (a.out, ", ".join("%s %s" % kv for kv in stats.items())))
    verify(a.out)
    cap, vil = c4.capital_tiles(), village_tiles()
    print("столица %s (5x5), город %s (4x4), между ними %d клетки равнины"
          % (c4.CAPITAL_XY, VILLAGE_XY, gap(cap, vil)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
