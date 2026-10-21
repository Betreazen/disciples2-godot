#!/usr/bin/env python3
"""Генератор карты контрольного маршрута С4: `c4_route.sg` из `test_fast_battle.sg`.

    python tools/make_c4_map.py
    python tools/make_c4_map.py --out ../disciples-modding/game-dev/scenarios/c4_route.sg

Рисовать карту в ScenEdit руками не нужно: всё, что требует С4 (шаг по разным грунтам,
непроходимые горы, источник дохода, один играбельный игрок, отсутствие боёв на маршруте),
делается правкой готового сценария.

**Что правится и почему это безопасно.** Три из четырёх операций — изменение уже существующих
записей, а не сочинение новых байтов:

1. **Грунты.** `MidgardMapBlock` хранит блок 8x4 по 4 байта на клетку (`tile` из
   `tools/ksy/sg.ksy`: `type:u1`, `unknown:u2`, `image_id:u1`); `type` = грунт<<3 | террейн.
   Тетрады берутся не из головы, а из 34 ванильных карт `game-dev/Exports-archive`: лес
   `0d 00 00 00` (встречается 999 раз), вода `1d 00 00 00` (единственная тетрада воды на всех
   картах, 26 691 раз), горы `25 00 00 00` (единственная, 31 885 раз), равнина `05 00 00 00`
   и `01 00 00 00` под столицей — как в шаблоне.
2. **Столица и герой** переезжают из угла (1,1) в (5,5): в углу у отряда не остаётся ни одного
   свободного соседа, кроме клеток края карты — все прочие заняты самой столицей, а объекты
   класса `FT` движок считает непроходимыми (`Movement.BlockingKinds`, ранг 1).
   Правятся только `POS_X`/`POS_Y` записей `CCapital` и `CMidStack` и их клетки в плане.
3. **Нейтральные отряды удаляются целиком** (записи `CMidStack` + их `CMidUnit` + клетки плана):
   бой в С4 не участвует, а нейтралов «не больше трёх» — требование плана Ф5.
4. **Кристалл** — единственная новая запись. Её тело — пять полей (`sg.ksy: mid_crystal`), и
   сверено оно с живым кристаллом ванильной карты байт в байт по составу полей.

Чего здесь **нет** и почему — см. `check --minimal` в `tools/scenario_c4.py` и отчёт задачи 10:
деревня, руина, лавка, наёмники, наставник, мешок и два события сценария отложены — для первых
чисел С4 (казна, позиции, опыт, туман, состав) они не нужны.
"""
from __future__ import annotations

import argparse
import sys
from dataclasses import replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402

MODDING = Path(__file__).resolve().parents[2] / "disciples-modding"
TEMPLATE = MODDING / "game-dev" / "scenarios" / "test_fast_battle.sg"  # 10.09.2026 шаблон убран из Exports: список игры только для карт партий
OUT = MODDING / "game-dev" / "scenarios" / "c4_route.sg"

WHAT_CRYSTAL = ".?AVCMidCrystal@@"
BLOCK_W, BLOCK_H = 8, 4              # размер блока карты в клетках (72 блока на 48x48)

# Тетрады клеток, снятые с ванильных карт (см. шапку). Ключ — грунт (Lground).
TILE = {
    0: b"\x05\x00\x00\x00",          # равнина, нейтральный террейн
    1: b"\x0d\x00\x00\x00",          # лес
    3: b"\x1d\x00\x00\x00",          # вода
    4: b"\x25\x00\x00\x00",          # горы (непроходимо)
}
TILE_CAPITAL = b"\x01\x00\x00\x00"   # равнина, террейн Империи — как под столицей в шаблоне

CAPITAL_XY = (5, 5)                  # левый верхний угол; столица занимает 5x5
CAPITAL_SIZE = 5
# Участки грунта: (грунт, x0, y0, x1, y1) включительно.
#
# Лес и вода лежат **полосами во всю ширину карты**, и это не украшательство: маршрут строится
# Дейкстрой по стоимости, а лесной или водный остров она обходит по равнине (проверено —
# островной вариант дал маршрут из 496 клеток, все до одной равнина). Полоса заставляет
# пересекать её ровно по числу строк, и стоимость шага по лесу и воде попадает в С4.
# Горы — блок в стороне от полос: сквозь них пути нет, и полосу они перегородить не должны.
REGIONS = [
    (1, 0, 12, 47, 17),              # лес: полоса в 6 строк (нужен участок >= 6)
    (3, 0, 24, 47, 27),              # вода: полоса в 4 строки (нужен участок >= 4)
    (4, 36, 32, 41, 44),             # горная гряда; коридор x=34..35 остаётся проходимым
]
CRYSTALS = [(0, 12, 20)]             # (ресурс, x, y) — ресурс 0, как у ванильных кристаллов
# Герой — пеший. В шаблоне стоит Pegasus Knight (g000uu0019), а он летает: местность ему
# безразлична, и стоимость шага по лесу и воде в С4 не измерилась бы вовсе (замечание
# пользователя на живом прогоне 07.09.2026). Archmage — те же MOVE 20 и SCOUT 5, но ходит.
HERO_TYPE = "g000uu0021"
GLOBALS = Path(__file__).resolve().parents[2] / "disciples-modding" / "game-pristine" / "Globals"

# Путевые точки контрольного маршрута: змейка из восьми вертикальных проходов, каждый пересекает
# обе полосы, последний идёт вдоль гор (x=35, горы с x=36). Подобраны под 50 ходов при бюджете
# 20 очков; готовый маршрут — tools/c4_route.json.
WAYPOINTS = ["12,12", "20,16", "24,20", "32,20", "32,26", "35,33", "32,26", "16,26", "16,14", "32,14", "32,20", "24,26", "16,20", "24,14", "32,20", "32,26", "35,33", "16,26", "16,14", "32,14", "24,20", "32,26", "16,26", "16,14", "32,14", "24,20", "32,20", "24,26", "16,14", "24,20"]  # сначала в центр, потом круги (замечание пользователя 07.09)


# ----------------------------------------------------------------- клетки карты

def block_origin(block_id: str) -> tuple[int, int]:
    """(x, y) левого верхнего угла блока: id вида 'S143MB<yy><xx>' в hex."""
    return int(block_id[8:10], 16), int(block_id[6:8], 16)


def wanted_tiles(size: int) -> dict[tuple[int, int], bytes]:
    """Что должно лежать в каждой клетке: равнина, поверх неё участки, поверх — пятно столицы."""
    tiles = {(x, y): TILE[0] for y in range(size) for x in range(size)}
    for ground, x0, y0, x1, y1 in REGIONS:
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                tiles[(x, y)] = TILE[ground]
    for x, y in capital_tiles():
        tiles[(x, y)] = TILE_CAPITAL
    return tiles


def set_terrain(scen: sg.Sg, tiles: dict[tuple[int, int], bytes]) -> tuple[sg.Sg, int]:
    """Переписать байты клеток в блоках карты; всё остальное в записи блока — байт в байт."""
    out, changed = scen, 0
    for blk in scen.find("MidgardMapBlock"):
        c = sg.Cursor(blk.body)
        bid, n = c.str("BLOCKID"), c.int("BLOCKDATA")
        head, data = blk.body[:len(blk.body) - n], bytearray(blk.body[len(blk.body) - n:])
        bx, by = block_origin(bid)
        for i in range(n // 4):
            want = tiles.get((bx + i % BLOCK_W, by + i // BLOCK_W))
            if want is not None and bytes(data[i * 4:i * 4 + 4]) != want:
                data[i * 4:i * 4 + 4] = want
                changed += 1
        out = out.with_record(blk, replace(blk, body=head + bytes(data)))
    return out, changed


# --------------------------------------------------------- записи: правка и удаление

def capital_tiles() -> list[tuple[int, int]]:
    x0, y0 = CAPITAL_XY
    return [(x0 + dx, y0 + dy) for dy in range(CAPITAL_SIZE) for dx in range(CAPITAL_SIZE)]


def move_position(body: bytes, head_layout: str, x: int, y: int) -> bytes:
    """Переписать POS_X/POS_Y внутри тела: курсор доходит до них по раскладке головы,
    сами два поля пересобираются, хвост тела копируется байт в байт."""
    c = sg.Cursor(body)
    c.fields(head_layout)            # раскладка до POS_X, не включая её
    start = c.pos
    c.fields("POS_X:i POS_Y:i")
    return body[:start] + sg.f_int("POS_X", x) + sg.f_int("POS_Y", y) + body[c.pos:]


# LAYOUT_CAPITAL кончается на 'POS_X:i POS_Y:i' — для move_position нужна голова без них
_CITY_HEAD = sg.LAYOUT_CAPITAL.rsplit(" POS_X:i", 1)[0]


def move_capital(scen: sg.Sg, x: int, y: int) -> sg.Sg:
    rec = scen.one(sg.WHAT_CAPITAL)
    return scen.with_record(rec, replace(rec, body=move_position(rec.body, _CITY_HEAD, x, y)))


_STACK_TO_POS = ("GROUP_ID:s " + " ".join(f"UNIT_{k}:s" for k in range(6))
                 + " " + " ".join(f"POS_{k}:i" for k in range(6)))


def move_stack(scen: sg.Sg, rec: sg.Record, x: int, y: int) -> sg.Sg:
    """У MidStack POS_X/POS_Y идут после списка предметов, поэтому раскладка читается курсором."""
    c = sg.Cursor(rec.body)
    c.fields(_STACK_TO_POS)
    for _ in range(c.count()):
        c.str("ITEM_ID")
    c.fields("STACK_ID:s SRCTMPL_ID:s LEADER_ID:s LEADR_ALIV:b")
    start = c.pos
    c.fields("POS_X:i POS_Y:i")
    body = rec.body[:start] + sg.f_int("POS_X", x) + sg.f_int("POS_Y", y) + rec.body[c.pos:]
    return scen.with_record(rec, replace(rec, body=body))


def drop_neutral_stacks(scen: sg.Sg, keep: int = 0) -> tuple[sg.Sg, list[str]]:
    """Удалить нейтральные отряды и их юнитов. keep — сколько оставить (плану Ф5 хватает <= 3)."""
    neutral_players = {sg.fields(r.body, sg.LAYOUT_PLAYER)["PLAYER_ID"]
                       for r in scen.find(sg.WHAT_PLAYER)
                       if sg.fields(r.body, sg.LAYOUT_PLAYER)["RACE_ID"] == "G000RR0004"}
    doomed = [r for r in scen.find(sg.WHAT_STACK)
              if sg.parse_stack(r.body)["OWNER"] in neutral_players][keep:]
    dead_units = {u for r in doomed for u in
                  (sg.parse_stack(r.body)[f"UNIT_{k}"] for k in range(6)) if u != sg.NULL_ID}
    dead_ids = {r.obj_id for r in doomed}
    records = tuple(r for r in scen.records
                    if r.obj_id not in dead_ids and r.obj_id not in dead_units)
    return replace(scen, records=records), sorted(dead_ids)


def add_crystals(scen: sg.Sg, spots: list[tuple[int, int, int]]) -> tuple[sg.Sg, list[str]]:
    ids = scen.next_ids("CR", len(spots))
    recs = [sg.Record(WHAT_CRYSTAL, cid,
                      sg.f_str("CRYSTAL_ID", cid) + sg.f_int("RESOURCE", res)
                      + sg.f_int("POS_X", x) + sg.f_int("POS_Y", y) + sg.f_int("AIPRIORITY", 3))
            for cid, (res, x, y) in zip(ids, spots)]
    return scen.appended(*recs), ids


def rebuild_plan(scen: sg.Sg) -> sg.Sg:
    """План собирается заново из живых записей: столица 5x5, отряды и кристаллы по клетке."""
    entries: list[tuple[int, int, str]] = []
    cap = scen.one(sg.WHAT_CAPITAL)
    for x, y in capital_tiles():
        entries.append((x, y, cap.obj_id))
    for r in scen.find(sg.WHAT_STACK):
        st = sg.parse_stack(r.body)
        entries.append((int(st["POS_X"]), int(st["POS_Y"]), r.obj_id))
    for r in scen.find(WHAT_CRYSTAL):
        f = sg.fields(r.body, "CRYSTAL_ID:s RESOURCE:i POS_X:i POS_Y:i")
        entries.append((int(f["POS_X"]), int(f["POS_Y"]), r.obj_id))
    rec = scen.one(sg.WHAT_PLAN)
    size, _ = sg.parse_plan(rec.body)
    return scen.with_record(rec, replace(rec, body=sg.plan_body(rec.obj_id, size, entries)))


def hero_hp(type_id: str) -> int:
    """HIT_POINT типа из Gunits.dbf — свежий MidUnit пишется с полным здоровьем."""
    from dbf_dump import read  # noqa: E402  (tools/dbf_dump.py, cp866)
    for row in read(GLOBALS / "Gunits.dbf")["rows"]:
        if row["UNIT_ID"].lower() == type_id.lower():
            return int(row["HIT_POINT"])
    raise SystemExit(f"тип {type_id} не найден в Gunits.dbf")


def set_hero_type(scen: sg.Sg, stack: sg.Record, type_id: str) -> sg.Sg:
    """Заменить лидера отряда на юнита типа type_id: тот же UNIT_ID, свежее тело MidUnit."""
    leader_id = sg.parse_stack(stack.body)["LEADER_ID"]
    unit = next(r for r in scen.find(sg.WHAT_UNIT) if r.obj_id.upper() == leader_id.upper())
    body = sg.unit_body(unit.obj_id, type_id, level=1, hp=hero_hp(type_id))
    return scen.with_record(unit, replace(unit, body=body))


# ------------------------------------------------------------------------ сборка

def build(template: Path, name: str = "c4_route") -> tuple[sg.Sg, dict]:
    scen = sg.Sg.read(template)
    size = len(sg.tile_grid(scen))
    scen, changed = set_terrain(scen, wanted_tiles(size))
    scen = move_capital(scen, *CAPITAL_XY)
    hero = next(r for r in scen.find(sg.WHAT_STACK)
                if sg.parse_stack(r.body)["INSIDE"] != sg.NULL_ID)
    scen = move_stack(scen, hero, *CAPITAL_XY)
    scen = set_hero_type(scen, hero, HERO_TYPE)
    scen, dropped = drop_neutral_stacks(scen)
    scen, crystals = add_crystals(scen, CRYSTALS)
    scen = rebuild_plan(scen)
    scen = sg.set_scenario_name(scen, name)
    return scen, {"клеток перерисовано": changed, "отрядов удалено": len(dropped),
                  "кристаллов добавлено": len(crystals), "записей": len(scen.records)}


def verify(path: Path) -> None:
    """Прочитать записанный файл обратно и проверить числами всё, что генератор обещал."""
    scen = sg.Sg.read(path)              # само чтение проверяет счётчик объектов и рамки записей
    grid = sg.tile_grid(scen)
    want = wanted_tiles(len(grid))
    bad = [(x, y) for (x, y), t in want.items() if grid[y][x] != t[0]]
    assert not bad, f"грунт разошёлся в {len(bad)} клетках, первые {bad[:5]}"

    cap = sg.fields(scen.one(sg.WHAT_CAPITAL).body, sg.LAYOUT_CAPITAL)
    assert (cap["POS_X"], cap["POS_Y"]) == CAPITAL_XY, cap
    stacks = [sg.parse_stack(r.body) for r in scen.find(sg.WHAT_STACK)]
    assert len(stacks) == 1, f"отрядов на карте {len(stacks)}, ожидался один (свой)"
    leader = next(r for r in scen.find(sg.WHAT_UNIT) if r.obj_id.upper() == stacks[0]["LEADER_ID"].upper())
    got = sg.fields(leader.body, sg.LAYOUT_UNIT)["TYPE"]
    assert got.lower() == HERO_TYPE, f"тип героя {got}, ожидался {HERO_TYPE} (пеший)"
    assert (stacks[0]["POS_X"], stacks[0]["POS_Y"]) == CAPITAL_XY, stacks[0]
    assert stacks[0]["MOVE"] == 20, stacks[0]["MOVE"]

    crystals = [sg.fields(r.body, "CRYSTAL_ID:s RESOURCE:i POS_X:i POS_Y:i AIPRIORITY:i")
                for r in scen.find(WHAT_CRYSTAL)]
    assert len(crystals) == len(CRYSTALS), crystals
    assert [(c["RESOURCE"], c["POS_X"], c["POS_Y"]) for c in crystals] == CRYSTALS, crystals

    size, plan = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    ids = {r.obj_id for r in scen.records}
    dangling = sorted({e for _, _, e in plan} - ids)
    assert not dangling, f"в плане ссылки на удалённые записи: {dangling}"
    assert size == len(grid), (size, len(grid))
    # каждая ссылка тела на другую запись должна существовать: удаление юнитов не осиротило стек
    for r in scen.find(sg.WHAT_STACK):
        st = sg.parse_stack(r.body)
        for k in range(6):
            u = st[f"UNIT_{k}"]
            assert u == sg.NULL_ID or u in ids, f"{r.obj_id}: юнит {u} удалён, а ссылка осталась"
    print("проверка чтения: грунты, столица, отряд, кристаллы и план сошлись")


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--template", type=Path, default=TEMPLATE)
    p.add_argument("--out", type=Path, default=OUT)
    p.add_argument("--name", default="c4_route")
    a = p.parse_args(argv)
    if not a.template.exists():
        raise SystemExit(f"нет шаблона {a.template}")
    scen, stats = build(a.template, a.name)
    a.out.parent.mkdir(parents=True, exist_ok=True)
    scen.write(a.out)
    print("%s: %s" % (a.out, ", ".join(f"{k} {v}" for k, v in stats.items())))
    verify(a.out)
    print("маршрут пересобирается так:\n"
          "  python tools/scenario_c4.py route --scenario %s \\\n"
          "      --waypoints %s --out tools/c4_route.json" % (a.out, " ".join(WAYPOINTS)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
