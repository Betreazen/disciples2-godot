#!/usr/bin/env python3
"""Карта M17 `f7-m17-charges` — расход зарядов талисмана, сферы и посоха.

    python tools/make_f7_m17_map.py             # записать game-dev/Exports/f7_m17_charges.sg
    python tools/make_f7_m17_map.py --plan      # бои, маршрут и таблицы гипотез, без записи
    python tools/make_f7_m17_map.py --selftest  # порча профиля и файла плюс сторож

Сборка — общим `make_f7_patch_maps.build` по профилю `f7_plan.PROFILES["m17"]` (правила,
числа и сторожа — `tools/f7_charges.py`). Сверх общего здесь одно: лидеру дописывается
модификатор «Тайное знание» `G000UM4002`. Без него Архимаг 1-го уровня не наденет Книгу
колдовства (она сама требует способность 10), а без Книги закрыт талисман. Модификаторы у
юнитов сценария — штатный формат: их несут 180 юнитов ванильных карт `Exports-archive`.

m17 нарочно не в `f7_plan.ORDER`: общий генератор перезаписал бы карту без модификатора.
Шаблон — `game-dev/scenarios/test_fast_battle.sg` (`make_c4_map.TEMPLATE` смотрит в старое место).
Игра отсюда не запускается.
"""
from __future__ import annotations

import argparse
import sys
from collections import Counter
from dataclasses import replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402
import scenario_squads as sq  # noqa: E402
import make_items_map as mi  # noqa: E402
import make_f7_patch_maps as mp  # noqa: E402
import f7_plan as fp  # noqa: E402
import f7_charges as fc  # noqa: E402

KEY = "m17"
TEMPLATE = mp.MODDING / "game-dev" / "scenarios" / "test_fast_battle.sg"
SCRATCH = Path(__file__).resolve().parents[1] / "corpus" / "_f7-selftest"


# ---------------------------------------------------------------- модификаторы

def unit_modifiers(rec: sg.Record) -> list[str]:
    c = sg.Cursor(rec.body)
    c.fields(sg.LAYOUT_UNIT)
    return [c.str("MODIF_ID") for _ in range(c.count())]


def with_modifiers(rec: sg.Record, mods: list[str]) -> sg.Record:
    """Тело `CMidUnit` с другим списком модификаторов; всё до и после списка — байт в байт."""
    c = sg.Cursor(rec.body)
    c.fields(sg.LAYOUT_UNIT)
    head = rec.body[:c.pos]
    for _ in range(c.count()):
        c.str("MODIF_ID")
    lst = sg.f_count(rec.obj_id, len(mods)) + b"".join(sg.f_str("MODIF_ID", m) for m in mods)
    return replace(rec, body=head + lst + rec.body[c.pos:])


def stack_at(scen: sg.Sg, xy: tuple[int, int], owner: str) -> dict:
    for r in scen.find(sg.WHAT_STACK):
        s = sg.parse_stack(r.body)
        if s["OWNER"] == owner and (int(s["POS_X"]), int(s["POS_Y"])) == tuple(xy):
            return s
    raise SystemExit("%s: в клетке %s нет отряда игрока %s" % (KEY, tuple(xy), owner))


def leader_of(scen: sg.Sg, stack: dict) -> sg.Record:
    return next(r for r in scen.find(sg.WHAT_UNIT) if r.obj_id.upper() == stack["LEADER_ID"].upper())


# --------------------------------------------------------------------- сборка

def check_constants() -> None:
    """Id вещей в `f7_charges` обязаны совпадать с `f7_plan.ITEM_*`: заряды заводит `add_bag`
    ровно по `fp.ITEM_TALISMAN`."""
    for mine, theirs in ((fc.TALISMAN, fp.ITEM_TALISMAN), (fc.ORB, fp.ITEM_ORB),
                         (fc.BOOK, fp.ITEM_BOOK), (fc.START_CHARGES, fp.TALISMAN_CHARGES)):
        if str(mine).upper() != str(theirs).upper():
            raise SystemExit("%s: f7_charges %s, а f7_plan %s — заряды лягут не туда"
                             % (KEY, mine, theirs))


def build(template: Path, t: sq.Tables) -> tuple[sg.Sg, dict]:
    prof = fp.profile(KEY)
    scen, stats = mp.build(template, KEY, t)
    human, _ = mp.players(scen)
    for p in prof["parties"]:
        rec = leader_of(scen, stack_at(scen, p["xy"], human["PLAYER_ID"]))
        scen = scen.with_record(rec, with_modifiers(rec, list(p.get("modifiers", ()))))
    return scen, dict(stats, **{"модификаторов лидеру": len(prof["parties"][0]["modifiers"])})


# ------------------------------------------------------------------ проверка

def verify_leader(scen: sg.Sg, prof: dict) -> None:
    human, _ = mp.players(scen)
    for p in prof["parties"]:
        got = [m.upper() for m in unit_modifiers(leader_of(scen, stack_at(scen, p["xy"],
                                                                          human["PLAYER_ID"])))]
        want = [m.upper() for m in p.get("modifiers", ())]
        if got != want:
            raise SystemExit("%s: у лидера отряда %s модификаторы %s, ожидались %s — без "
                             "«Тайного знания» Книгу не надеть" % (KEY, tuple(p["xy"]), got, want))


def verify_items(scen: sg.Sg, prof: dict) -> None:
    """Вещей ровно столько, сколько в мешках профиля; у каждого талисмана 5 зарядов."""
    kinds = mi.item_types(scen)
    want = Counter(i.upper() for b in prof["bags"] for i in b["items"])
    got = Counter(v.upper() for v in kinds.values())
    for item, n in want.items():
        if got[item] != n:
            raise SystemExit("%s: вещей %s в файле %d, ожидалось %d" % (KEY, item, got[item], n))
    charges = dict(mi.parse_charges(scen.one(mi.WHAT_TALISMAN).body))
    for iid in (i for i, v in kinds.items() if v.upper() == fc.TALISMAN):
        if charges.get(iid) != fc.START_CHARGES:
            raise SystemExit("%s: у талисмана %s зарядов %s, ожидалось %d — расход не с чего "
                             "считать" % (KEY, iid, charges.get(iid), fc.START_CHARGES))


def verify_dummy(scen: sg.Sg, prof: dict, t: sq.Tables) -> None:
    _, neutral = mp.players(scen)
    d = fc.dummy(prof)
    rec = leader_of(scen, stack_at(scen, d["xy"], neutral["PLAYER_ID"]))
    hp, full = mp.unit_hp(rec), int(t.unit(d["leader"])["HIT_POINT"])
    if hp != full:
        raise SystemExit("%s: мишень посохов на %d/%d HP — урон посоха не с чего считать"
                         % (KEY, hp, full))


def verify(path: Path, t: sq.Tables, quiet: bool = False) -> None:
    prof = fp.profile(KEY)
    mp.verify(path, KEY, t, quiet=True)
    scen = sg.Sg.read(path)
    verify_leader(scen, prof)
    verify_items(scen, prof)
    verify_dummy(scen, prof, t)
    if not quiet:
        raw = sg.header_name(scen)
        print("  проверка чтения %s: ScenarioInfo.NAME %r, заголовок 0x%X %r, потолки, казна, "
              "отряд с модификатором, %d вещей, заряды, мишень — сошлись"
              % (path.name, sg.scenario_name(scen), sg.header_name_offset(scen.header), raw,
                 sum(len(b["items"]) for b in prof["bags"])))


# ------------------------------------------------------------------ selftest

def sabotage(scen: sg.Sg, what: str, t: sq.Tables) -> sg.Sg:
    prof = fp.profile(KEY)
    human, neutral = mp.players(scen)
    if what == "nomods":
        rec = leader_of(scen, stack_at(scen, prof["parties"][0]["xy"], human["PLAYER_ID"]))
        return scen.with_record(rec, with_modifiers(rec, []))
    if what == "charges":
        return mi.set_charges(scen, [(i, n - 1) for i, n in
                                     mi.parse_charges(scen.one(mi.WHAT_TALISMAN).body)])
    if what == "dummyhurt":
        d = fc.dummy(prof)
        rec = leader_of(scen, stack_at(scen, d["xy"], neutral["PLAYER_ID"]))
        return scen.with_record(rec, replace(rec, body=sg.unit_body(rec.obj_id, d["leader"].upper(), 1, 1)))
    if what == "header":
        at = sg.header_name_offset(scen.header)
        head = scen.header[:at] + b"chuzhoe".ljust(len(prof["name"]) + 1, b"\x00") \
            + scen.header[at + len(prof["name"]) + 1:]
        return replace(scen, header=head)
    return mp.sabotage_map(scen, what, t)


FILE_SABOTAGE = [("maxunit", "MAX_UNIT сбит на 2"), ("leadermax", "потолок уровня героя 1"),
                 ("bank", "казна не та"), ("name", "ScenarioInfo.NAME не тот"),
                 ("header", "имя в заголовке не то"), ("noparty", "отряд прогона пропал"),
                 ("nobag", "мешок пропал"), ("nomods", "у лидера нет «Тайного знания»"),
                 ("charges", "у талисманов не 5 зарядов"), ("dummyhurt", "мишень посохов ранена")]


def file_cases(template: Path, scratch: Path, t: sq.Tables) -> list[tuple[str, str | None]]:
    scratch.mkdir(parents=True, exist_ok=True)
    out = scratch / "f7_m17_selftest.sg"
    scen, _ = build(template, t)
    scen.write(out)
    honest = fp.raises(lambda: verify(out, t, quiet=True))
    if honest is not None:
        raise SystemExit("честная карта m17 не проходит собственную проверку: %s" % honest)
    cases = []
    for what, title in FILE_SABOTAGE:
        sabotage(scen, what, t).write(out)
        cases.append(("файл: " + title, fp.raises(lambda: verify(out, t, quiet=True))))
    out.unlink()
    return cases


def selftest(template: Path, scratch: Path) -> int:
    t, prof = sq.Tables(), fp.profile(KEY)
    honest = fp.raises(lambda: (fp.check_profile(t, KEY, prof), fc.check_charges(t, prof, KEY)))
    if honest is not None:
        raise SystemExit("честный профиль m17 не проходит сторожей: %s" % honest)
    ok = {"MAX_UNIT": 5, "MAX_SPELL": 5, "MAX_LEADER": 99, "MAX_CITY": 5}
    groups = [("профиль", fc.profile_cases(t, prof, fp.raises)
               + [("MAX_UNIT не 5", fp.raises(
                   lambda: fp.check_scenario_limits(dict(ok, MAX_UNIT=2), KEY)))]),
              ("файл карты", file_cases(template, scratch, t))]
    bad = total = 0
    for title, cases in groups:
        if not cases:
            raise SystemExit("группа «%s» не сделала ни одного сравнения — это не проверка" % title)
        print("--- %s: %d сравнений" % (title, len(cases)))
        for name, msg in cases:
            print("%-44s %s" % (name, ("ловится: " + msg.split("\n")[0]) if msg else "ПРОПУЩЕН"))
            bad += msg is None
        total += len(cases)
    print("selftest m17: сработали %d сторожей из %d сравнений" % (total - bad, total))
    return 1 if bad else 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--template", type=Path, default=TEMPLATE)
    p.add_argument("--out-dir", type=Path, default=mp.EXPORTS)
    p.add_argument("--plan", action="store_true", help="только расчёт, ничего не записывать")
    p.add_argument("--selftest", action="store_true", help="порча профиля и файла плюс сторож")
    p.add_argument("--scratch", type=Path, default=SCRATCH)
    a = p.parse_args(argv)
    if not a.template.exists():
        raise SystemExit("нет шаблона %s" % a.template)
    check_constants()
    if a.selftest:
        return selftest(a.template, a.scratch)
    t, prof = sq.Tables(), fp.profile(KEY)
    fp.check_profile(t, KEY, prof)
    fc.print_charges(t, prof, fc.check_charges(t, prof, KEY))
    if a.plan:
        return 0
    scen, stats = build(a.template, t)
    out = a.out_dir / prof["file"]
    scen.write(out)
    print("%s: %s" % (out, ", ".join("%s %s" % kv for kv in stats.items())))
    verify(out, t)
    print("в %s: %s" % (a.out_dir, ", ".join(sorted(x.name for x in a.out_dir.glob("*.sg")))))
    return 0


if __name__ == "__main__":
    sys.exit(main())
