#!/usr/bin/env python3
"""Варианты test_fast_battle.sg с заданными отрядами — для съёма корпуса боёв.

Стек героя S143KC0000 (владелец-человек) и все стеки ИИ/нейтралов дополняются
солдатами; лидеры, id стеков и их позиции не трогаются — по ним кликает
gamedrv/goto_battle.py. Создаются только записи MidUnit; в MidStack переписываются
UNIT_0..5/POS_0..5, остальное тело байт в байт. План карты не меняется (стеки уже
в нём). Строй как в disciples-mod arena.formation: ближний бой (L_ADJACENT) —
вперёд (0,2,4), остальные — назад (1,3,5), крупный юнит занимает столбец;
крупные ставятся первыми, иначе столбец может не найтись.

    python tools/scenario_squads.py --preset a
    python tools/scenario_squads.py --template X.sg --out Y.sg \
        --own g000uu0001,g000uu0002 --enemy g000uu5039
"""
from __future__ import annotations

import argparse
import sys
from dataclasses import replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from dbf_dump import read  # noqa: E402
import sgfile as sg  # noqa: E402

MODDING = Path(__file__).resolve().parents[2] / "disciples-modding"
GLOBALS = MODDING / "game-pristine" / "Globals"
TEMPLATE = MODDING / "game-dev" / "scenarios" / "test_fast_battle.sg"  # 10.09.2026 шаблон убран из Exports: список игры только для карт партий
OUT_DIR = MODDING / "game-dev" / "scenarios"
HERO_STACK = "S143KC0000"
FRONT, BACK = (0, 2, 4), (1, 3, 5)
CELLS = 6

PRESETS = {  # (own, enemy)
    "a": ("g000uu0001,g000uu0002,g000uu0006", "g000uu5039,g000uu5013,g000uu5002"),
    "b": ("g000uu0001,g000uu0002,g000uu0006,g000uu0011", "g000uu5039,g000uu5013,g000uu5006"),
    "c": ("g000uu0001,g000uu0078", "g000uu5002,g000uu8046"),
    "d": ("g000uu0001,g000uu0002,g000uu5003", "g000uu0055,g000uu0056,g000uu5012"),
    "e": ("g000uu0001,g000uu0002,g000uu0018", "g000uu5031,g000uu5035,g000uu5033"),
}


class Tables:
    def __init__(self, globals_dir: Path = GLOBALS) -> None:
        self.units = {r["UNIT_ID"].lower(): r for r in read(globals_dir / "Gunits.dbf")["rows"]}
        self.attacks = {r["ATT_ID"].lower(): r for r in read(globals_dir / "Gattacks.dbf")["rows"]}
        self.text = {r["TXT_ID"].lower(): r["TEXT"] for r in read(globals_dir / "Tglobal.dbf")["rows"]}
        self.melee = next(r["ID"] for r in read(globals_dir / "LAttR.dbf")["rows"]
                          if r["TEXT"] == "L_ADJACENT")

    def unit(self, unit_id: str) -> dict:
        row = self.units.get(unit_id.lower())
        if row is None:
            raise ValueError(f"юнит {unit_id} не найден в Gunits")
        return row

    def name(self, unit_id: str) -> str:
        return self.text.get(self.unit(unit_id)["NAME_TXT"].lower(), unit_id)

    def is_big(self, unit_id: str) -> bool:
        return not self.unit(unit_id)["SIZE_SMALL"]

    def is_melee(self, unit_id: str) -> bool:
        att = self.attacks.get(self.unit(unit_id)["ATTACK_ID"].lower(), {})
        return att.get("REACH") == self.melee


def place(cells: list[int], new_ids: list[str], first_index: int, t: Tables) -> list[int]:
    """Новые POS_0..5: занятые клетки сохраняются, new_ids получают индексы first_index+k."""
    out = list(cells)
    order = sorted(range(len(new_ids)), key=lambda k: not t.is_big(new_ids[k]))  # крупные первыми
    for k in order:
        uid = new_ids[k]
        if t.is_big(uid):
            col = next((c for c in FRONT if out[c] < 0 and out[c + 1] < 0), None)
            want = [] if col is None else [col, col + 1]
        else:
            line = (FRONT + BACK) if t.is_melee(uid) else (BACK + FRONT)
            want = [c for c in line if out[c] < 0][:1]
        if not want:
            raise ValueError(f"{uid} ({t.name(uid)}): не помещается в строй {out}")
        for c in want:
            out[c] = first_index + k
    return out


def add_soldiers(scen: sg.Sg, stack: sg.Record, unit_ids: list[str], new_ids: list[str],
                 t: Tables) -> tuple[sg.Sg, sg.Record]:
    """Дописать солдат в стек: новые MidUnit в конец файла, в теле стека переписаны только
    UNIT_k/POS_k (префикс GROUP_ID..POS_5), хвост тела — байт в байт."""
    st = sg.parse_stack(stack.body)
    units = [st[f"UNIT_{k}"] for k in range(CELLS) if st[f"UNIT_{k}"] != sg.NULL_ID]
    cells = place([st[f"POS_{k}"] for k in range(CELLS)], unit_ids, len(units), t)
    units = units + new_ids
    c = sg.Cursor(stack.body)
    c.fields(sg._GROUP)
    prefix = (sg.f_str("GROUP_ID", st["GROUP_ID"])
              + b"".join(sg.f_str(f"UNIT_{k}", u) for k, u in
                         enumerate(units + [sg.NULL_ID] * (CELLS - len(units))))
              + b"".join(sg.f_int(f"POS_{k}", p) for k, p in enumerate(cells)))
    new_units = [sg.Record(sg.WHAT_UNIT, uid, sg.unit_body(
        uid, impl.upper(), int(t.unit(impl)["LEVEL"]), int(t.unit(impl)["HIT_POINT"])))
        for uid, impl in zip(new_ids, unit_ids)]
    new_stack = replace(stack, body=prefix + stack.body[c.pos:])
    return scen.with_record(stack, new_stack).appended(*new_units), new_stack


def build(template: Path, own: list[str], enemy: list[str], t: Tables) -> sg.Sg:
    scen = sg.Sg.read(template)
    hero = next(r for r in scen.records if r.obj_id == HERO_STACK)
    hero_owner = sg.parse_stack(hero.body)["OWNER"]
    ai = [r for r in scen.records if r.what == sg.WHAT_STACK
          and sg.parse_stack(r.body)["OWNER"] != hero_owner]
    for ids in (own, enemy):
        for u in ids:
            t.unit(u)
    ids = iter(scen.next_ids("UN", len(own) + len(enemy) * len(ai)))
    scen, _ = add_soldiers(scen, hero, own, [next(ids) for _ in own], t)
    for stack in ai:
        scen, _ = add_soldiers(scen, stack, enemy, [next(ids) for _ in enemy], t)
    return scen


def roster(scen: sg.Sg, t: Tables) -> list[tuple[str, tuple[int, int], str, list[tuple]]]:
    """(id стека, (x, y), владелец, [(клетка, тип, имя), ...]) по каждому MidStack;
    крупный юнит — один раз, по первой клетке."""
    types = {r.obj_id: sg.fields(r.body, sg.LAYOUT_UNIT)["TYPE"] for r in scen.find(sg.WHAT_UNIT)}
    out = []
    for r in scen.find(sg.WHAT_STACK):
        st = sg.parse_stack(r.body)
        seen, members = set(), []
        for c in range(CELLS):
            k = st[f"POS_{c}"]
            if k < 0 or k in seen:
                continue
            seen.add(k)
            impl = str(types[st[f"UNIT_{k}"]]).lower()
            members.append((c, impl, t.name(impl)))
        out.append((r.obj_id, (st["POS_X"], st["POS_Y"]), st["OWNER"], members))
    return out


def verify(template: Path, out: Path, own: list[str], enemy: list[str], t: Tables) -> None:
    """Прочитать обратно: счётчик заголовка (проверяет loads), позиции и id стеков как в
    шаблоне, каждый стек содержит лидера шаблона плюс заданные типы."""
    a, b = sg.Sg.read(template), sg.Sg.read(out)
    ra = {s[0]: s for s in roster(a, t)}
    rb = {s[0]: s for s in roster(b, t)}
    if ra.keys() != rb.keys():
        raise AssertionError("набор стеков изменился")
    for sid, (_, pos, owner, members) in rb.items():
        _, pos0, owner0, members0 = ra[sid]
        if (pos, owner) != (pos0, owner0):
            raise AssertionError(f"{sid}: позиция/владелец изменились {pos0}->{pos}")
        got = sorted(m[1] for m in members)
        base = [m[1] for m in members0]
        want = sorted(base + (own if sid == HERO_STACK else enemy))
        if got != want:
            raise AssertionError(f"{sid}: состав {got}, ожидался {want}")
    if len(b.records) != len(a.records) + len(own) + len(enemy) * (len(ra) - 1):
        raise AssertionError("число записей не сходится")


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--preset", choices=sorted(PRESETS))
    p.add_argument("--own", default="", help="типы солдат в стек героя, через запятую")
    p.add_argument("--enemy", default="", help="типы солдат в каждый стек ИИ, через запятую")
    p.add_argument("--template", type=Path, default=TEMPLATE)
    p.add_argument("--out", type=Path)
    p.add_argument("--globals", type=Path, default=GLOBALS)
    a = p.parse_args(argv)
    own, enemy = a.own, a.enemy
    if a.preset:
        own, enemy = PRESETS[a.preset]
        a.out = a.out or OUT_DIR / f"test_fast_battle_{a.preset}.sg"
    if not a.out:
        p.error("нужен --out или --preset")
    own = [u for u in own.split(",") if u]
    enemy = [u for u in enemy.split(",") if u]
    t = Tables(a.globals)
    scen = build(a.template, own, enemy, t)
    a.out.parent.mkdir(parents=True, exist_ok=True)
    scen.write(a.out)
    verify(a.template, a.out, own, enemy, t)
    print(f"{a.out}: {len(scen.records)} записей")
    for sid, pos, owner, members in roster(scen, t):
        print(f"  {sid} {pos} {owner}: " + ", ".join(f"{c}:{name} ({impl})" for c, impl, name in members))
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
