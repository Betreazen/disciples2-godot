#!/usr/bin/env python3
"""Карта M21 `f7-m21-staff` — суточный запрет посоха: на ЭКЗЕМПЛЯР или на ТИП.

    python tools/make_f7_m21_map.py             # записать game-dev/Exports/f7_m21_staff.sg
    python tools/make_f7_m21_map.py --plan      # раскладка и таблица гипотез, без записи
    python tools/make_f7_m21_map.py --selftest  # порча профиля и файла плюс сторож

Что уже закрыто прогоном `f7-m17-charges` (план Ф7, §18): зарядов у посоха нет, цена — мана
`GItem.CASTING`; тот же посох второй раз в тот же день — отказ; сброс суточный; другой посох
в тот же день проходит. Код: счётчик во ВТОРОМ списке `CMidSpellCast`, ключ (игрок, id `IM`),
проверка `0x5DBBE8` -> `0x606F50`/`0x606ECA`; лорд Маг — два применения в сутки, прочие — одно.
Пользователь: одинаковые посохи у одного героя складываются в стопку и применяются как один.

Не различено одно: **два одинаковых посоха у двух разных героев**, оба не применялись.
«На экземпляр» — сработают оба; «на тип» — второй откажет. Карта даёт ровно этот опыт: два
Архимага (способность 7 `L_WAND_SCROLL_USE` врождённая), у каждого свой мешок с `G000IG6001`,
у второго ещё контрольный `G000IG6011`, одна мишень без иммунитета в обзоре обоих.

**Лорд — Воитель.** У Мага лимит два, и «на тип» совпадёт с «на экземпляр» (сторож ловит).
Сборка — общим `make_f7_patch_maps.build`; профиль регистрируется в `f7_plan.PROFILES` при
импорте, сам `f7_plan.py` не правится. Игра отсюда не запускается.
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
import arena_plan as ap  # noqa: E402
import make_c4_map as c4  # noqa: E402
import make_f7_patch_maps as mp  # noqa: E402
import make_f7_m17_map as m17  # noqa: E402   (leader_of, stack_at, шаблон)
import f7_plan as fp  # noqa: E402
import f7_charges as fc  # noqa: E402         (таблицы вещей и заклинаний, мишень, check_distinct)
from f7_wave2 import RACE_EMPIRE, cheb, grace  # noqa: E402

KEY = "m21"
TEMPLATE = m17.TEMPLATE
SCRATCH = m17.SCRATCH

WAND = fc.WAND_A             # G000IG6001: g000ss0004, земля (7), урон 15, мана жизни 100
OTHER = fc.WAND_B            # G000IG6011: g000ss0072, смерть (3), урон 50, смерть и руны по 150
ABILITY_WAND = 7             # L_WAND_SCROLL_USE, гейт 0x5DF6B7
SEPARATE = 3                 # чужой мешок не ближе трёх клеток: иначе подберёт не тот герой
DUMMY_GAP = 2                # мишень не вплотную к героям и мешкам: лишний клик начнёт бой

A_XY, B_XY = (12, 12), (18, 12)
BAG_A, BAG_B = (12, 13), (18, 13)
DUMMY_XY = (15, 15)

# Экземпляр: (имя, индекс мешка, место в мешке). Тип и герой выводятся из профиля, поэтому
# порча мешков доходит и до модели гипотез, и до сторожей.
CASTS = (("A", 0, 0), ("A2", 1, 0), ("B", 1, 1))
TIMELINE = (("save", "С0"), ("cast", "A"), ("save", "W1"), ("cast", "A2"), ("save", "W2"),
            ("cast", "B"), ("save", "W3"))
RULES = ("на экземпляр", "на тип", "на героя")


def _party(xy: tuple[int, int], why: str) -> dict:
    return {"xy": xy, "leader": fc.LEADER, "cat": fc.CAT_MAGE, "units": [], "why": why,
            "cell": 1, "fights": False, "leader_hp": None}


PROFILE = {
    "file": "f7_m21_staff.sg", "name": "f7-m21-staff",
    "why": "S12+: суточный запрет посоха — на экземпляр или на тип",
    "bank": "G1000:R0000:Y1000:E1000:W1000:B0000",
    "parties": [_party(A_XY, "герой A: свой посох IG6001"),
                _party(B_XY, "герой B: такой же IG6001 и контрольный IG6011")],
    "bags": [{"xy": BAG_A, "items": [WAND], "why": "посох героя A"},
             {"xy": BAG_B, "items": [WAND, OTHER], "why": "такой же посох и контрольный — герою B"}],
    "need_items": [WAND, OTHER],
    "neutrals": [fc._neutral(DUMMY_XY, fc.DUMMY, "мишень посохов: не драться", fight=False)],
    "staff": {"casts": CASTS, "timeline": TIMELINE, "lord": fc.LORD_WARRIOR},
    "turns": 1, "spells": [], "village": None, "rods": {}, "need_dead": False,
}
fp.PROFILES.setdefault(KEY, PROFILE)


# ------------------------------------------------------------------ чтение профиля

def owner(prof: dict, bag: dict) -> int:
    """Кто подберёт мешок: ровно один герой вплотную, остальные не ближе SEPARATE."""
    dist = [cheb(tuple(p["xy"]), tuple(bag["xy"])) for p in prof["parties"]]
    near = [k for k, d in enumerate(dist) if d <= 1]
    if len(near) != 1 or any(d < SEPARATE for k, d in enumerate(dist) if k not in near):
        raise SystemExit("%s: мешок %s — расстояния до героев %s: вплотную обязан стоять ровно "
                         "один, остальные не ближе %d, иначе посох уйдёт не тому"
                         % (KEY, tuple(bag["xy"]), dist, SEPARATE))
    return near[0]


def instance(prof: dict, name: str) -> tuple[str, int]:
    """(тип, индекс героя) экземпляра посоха."""
    _, bi, pos = next(c for c in prof["staff"]["casts"] if c[0] == name)
    items = prof["bags"][bi]["items"]
    if pos >= len(items):
        raise SystemExit("%s: экземпляра %s нет — в мешке %d всего %d вещей"
                         % (KEY, name, bi, len(items)))
    return items[pos].upper(), owner(prof, prof["bags"][bi])


def wand_cost(item: str) -> dict[str, int]:
    return fc.bank(fc._items()[item.upper()]["CASTING"])


def wand_spell(item: str) -> dict:
    return fc.spell(fc._items()[item.upper()]["SPELL_ID"])


# ------------------------------------------------------------------------ сторожа

def check_gate(t: sq.Tables, prof: dict, key: str) -> None:
    """Каждый герой карты умеет посохи — иначе отказ по навыку спутают с отказом по счётчику."""
    for p in prof["parties"]:
        if ABILITY_WAND not in fc.innate(p["leader"]):
            raise SystemExit("%s: у героя %s (%s) в %s нет способности %d (посохи), есть %s"
                             % (key, p["leader"], t.name(p["leader"]), tuple(p["xy"]),
                                ABILITY_WAND, sorted(fc.innate(p["leader"]))))


def check_pair(prof: dict, key: str) -> None:
    """A и A2 — один тип у разных героев, B — другой тип у героя A2, стопок нет."""
    got = {c[0]: instance(prof, c[0]) for c in prof["staff"]["casts"]}
    (ta, ha), (ta2, ha2), (tb, hb) = got["A"], got["A2"], got["B"]
    not_wand = [tp for tp, _ in got.values() if fc.item_cat(tp) != fc.CAT_WAND]
    if not_wand:
        raise SystemExit("%s: %s — не посохи (ITEM_CAT %d)" % (key, not_wand, fc.CAT_WAND))
    if ta != ta2:
        raise SystemExit("%s: посохи A %s и A2 %s разного типа — «на экземпляр» и «на тип» "
                         "дадут одно и то же" % (key, ta, ta2))
    if ha == ha2:
        raise SystemExit("%s: оба посоха %s у героя %d — сложатся в одну стопку, второй "
                         "отдельно не применить" % (key, ta, ha))
    if tb == ta or wand_spell(tb)["SPELL_ID"].lower() == wand_spell(ta)["SPELL_ID"].lower():
        raise SystemExit("%s: контрольный посох %s того же типа или заклинания, что %s"
                         % (key, tb, ta))
    if hb != ha2:
        raise SystemExit("%s: контрольный посох у героя %d, а не у героя A2 (%d)" % (key, hb, ha2))
    held = Counter((owner(prof, b), i.upper()) for b in prof["bags"] for i in b["items"])
    stacked = [k for k, n in held.items() if n > 1]
    if stacked:
        raise SystemExit("%s: у героя одинаковые посохи %s — сложатся в стопку" % (key, stacked))


def check_immunity(t: sq.Tables, prof: dict, key: str) -> None:
    """Мишень не иммунна к источнику ни одного посоха, и каждый посох наносит урон."""
    by_src, _ = ap.immunities(t)
    d = fc.dummy(prof)["leader"].lower()
    for c in prof["staff"]["casts"]:
        tp, _ = instance(prof, c[0])
        s = wand_spell(tp)
        if int(s["DAMAGE_SRC"]) in by_src.get(d, set()):
            raise SystemExit("%s: мишень %s (%s) иммунна к источнику %s посоха %s"
                             % (key, d, t.name(d), s["DAMAGE_SRC"], tp))
        if not int(s["DAMAGE_QTY"] or 0):
            raise SystemExit("%s: посох %s без урона — здоровье мишени применения не покажет"
                             % (key, tp))


def check_reach(t: sq.Tables, prof: dict, key: str) -> None:
    """Мишень в обзоре каждого героя и с его места, и с клетки мешка, но не вплотную."""
    d = tuple(fc.dummy(prof)["xy"])
    for b in prof["bags"]:
        p = prof["parties"][owner(prof, b)]
        scout = int(t.unit(p["leader"])["SCOUT"])
        for xy in (tuple(p["xy"]), tuple(b["xy"])):
            if cheb(xy, d) > scout:
                raise SystemExit("%s: мишень %s дальше обзора %d от %s" % (key, d, scout, xy))
            if cheb(xy, d) < DUMMY_GAP:
                raise SystemExit("%s: мишень %s вплотную к %s — лишний клик начнёт бой"
                                 % (key, d, xy))


def check_mana(t: sq.Tables, prof: dict, key: str) -> None:
    """Худший случай — все три применения прошли: маны хватает на каждое, мишень жива."""
    last = sim(t, prof, RULES[0], strict=True)
    hp = min(v[3] for v in last.values())
    if hp <= 0:
        raise SystemExit("%s: мишень погибнет от худшего случая (HP %d)" % (key, hp))


def check_staff(t: sq.Tables, prof: dict, key: str) -> dict[str, dict]:
    """Все сторожа M21 разом. Возвращает таблицу гипотез для печати."""
    check_gate(t, prof, key)
    check_pair(prof, key)
    check_immunity(t, prof, key)
    check_reach(t, prof, key)
    check_mana(t, prof, key)
    table = tables(t, prof)
    fc.check_distinct(table, "посох: экземпляр или тип", key)
    return table


# ------------------------------------------------------------------------ гипотезы

def sim(t: sq.Tables, prof: dict, rule: str, strict: bool = False) -> dict[str, tuple]:
    """(мана жизни Y, смерти E, рун W, HP мишени, записей во втором списке CMidSpellCast) в
    каждом сохранении. Доход приходит тиком уже при загрузке (M17: 1000 + 25 = 1025). Порядок
    проверок движка: мана раньше счётчика — отказ ничего не списывает."""
    st = prof["staff"]
    income = fc.bank(grace(RACE_EMPIRE)["INCOME"])
    purse = {c: fc.bank(prof["bank"]).get(c, 0) + income.get(c, 0) for c in "YEW"}
    hp = int(t.unit(fc.dummy(prof)["leader"])["HIT_POINT"])
    limit = 2 if st["lord"] == fc.LORD_MAGE else 1
    used, casts, out = Counter(), 0, {}
    for kind, arg in st["timeline"]:
        if kind == "save":
            out[arg] = (purse["Y"], purse["E"], purse["W"], hp, casts)
            continue
        tp, hero = instance(prof, arg)
        cost = wand_cost(tp)
        if any(purse[c] < cost.get(c, 0) for c in purse):
            if strict:
                raise SystemExit("%s: на посох %s (%s) не хватит маны: есть %s, цена %s — отказ "
                                 "по мане спутает счётчик" % (KEY, arg, tp, purse, cost))
            continue
        k = {"на экземпляр": arg, "на тип": tp, "на героя": hero}[rule]
        if used[k] >= limit:
            continue
        purse = {c: purse[c] - cost.get(c, 0) for c in purse}
        hp -= int(wand_spell(tp)["DAMAGE_QTY"] or 0)
        used[k] += 1
        casts += 1
    return out


def tables(t: sq.Tables, prof: dict) -> dict[str, dict]:
    return {rule: sim(t, prof, rule) for rule in RULES}


# -------------------------------------------------------------------------- печать

def print_plan(t: sq.Tables, prof: dict, table: dict[str, dict]) -> None:
    d = fc.dummy(prof)
    print("%s -> %s: %s; лорд %s (Воитель), казна %s"
          % (KEY, prof["name"], prof["why"], prof["staff"]["lord"], prof["bank"]))
    for k, b in enumerate(prof["bags"]):
        p = prof["parties"][owner(prof, b)]
        print("   герой %s в %s: %s, мешок %s: %s, до мишени %d (обзор %s)"
              % ("AB"[owner(prof, b)], tuple(p["xy"]), t.name(p["leader"]), tuple(b["xy"]),
                 ", ".join(b["items"]), cheb(tuple(b["xy"]), tuple(d["xy"])),
                 t.unit(p["leader"])["SCOUT"]))
    print("   мишень %s: %s, %s HP" % (tuple(d["xy"]), t.name(d["leader"]),
                                     t.unit(d["leader"])["HIT_POINT"]))
    for item in sorted({i.upper() for b in prof["bags"] for i in b["items"]}):
        s = wand_spell(item)
        print("   посох %s: %s, урон %s, источник %s, мана %s"
              % (item, s["SPELL_ID"], s["DAMAGE_QTY"], s["DAMAGE_SRC"], wand_cost(item)))
    saves = [a for k, a in prof["staff"]["timeline"] if k == "save"]
    print("   гипотезы (мана Y/E/W / HP мишени / записей CMidSpellCast-2):")
    print("   %-14s %s" % ("гипотеза", " ".join("%-22s" % s for s in saves)))
    for rule, vals in table.items():
        print("   %-14s %s" % (rule, " ".join("%-22s" % "/".join(map(str, vals[s]))
                                             for s in saves)))


# ----------------------------------------------------------------- проверка файла

def verify(path: Path, t: sq.Tables, quiet: bool = False) -> None:
    prof = fp.profile(KEY)
    mp.verify(path, KEY, t, quiet=True)
    scen = sg.Sg.read(path)
    _, neutral = mp.players(scen)
    d = fc.dummy(prof)
    rec = m17.leader_of(scen, m17.stack_at(scen, d["xy"], neutral["PLAYER_ID"]))
    hp, full = mp.unit_hp(rec), int(t.unit(d["leader"])["HIT_POINT"])
    if hp != full:
        raise SystemExit("%s: мишень на %d/%d HP — урон посоха не с чего считать" % (KEY, hp, full))
    if not quiet:
        raw = sg.header_name(scen)
        print("  проверка чтения %s: ScenarioInfo.NAME %r, заголовок 0x%X %r, потолки, казна, "
              "два отряда, мешки по героям, мишень — сошлись"
              % (path.name, sg.scenario_name(scen), sg.header_name_offset(scen.header), raw))


# ------------------------------------------------------------------------ selftest

def _bag_records(scen: sg.Sg) -> list[tuple[sg.Record, tuple[int, int], list[str]]]:
    out = []
    for r in scen.find(mp.WHAT_BAG):
        c = sg.Cursor(r.body)
        f = c.fields("BAG_ID:s POS_X:i POS_Y:i IMAGE:i AIPRIORITY:i")
        out.append((r, (int(f["POS_X"]), int(f["POS_Y"])), [c.str("ITEM_ID") for _ in range(c.count())]))
    return out


def sabotage(scen: sg.Sg, what: str, t: sq.Tables) -> sg.Sg:
    prof = fp.profile(KEY)
    human, neutral = mp.players(scen)
    if what == "onebag":                     # посох героя B переложен в мешок героя A
        (ra, xa, ia), (rb, xb, ib) = sorted(_bag_records(scen), key=lambda x: x[1])
        scen = scen.with_record(ra, replace(ra, body=mp.bag_body(ra.obj_id, xa, ia + ib[:1])))
        return scen.with_record(rb, replace(rb, body=mp.bag_body(rb.obj_id, xb, ib[1:])))
    if what == "noskill":                    # герой B — Рыцарь на Пегасе, посохов не умеет
        rec = next(r for r in mp.own_stacks(scen)
                   if (int(sg.parse_stack(r.body)["POS_X"]), int(sg.parse_stack(r.body)["POS_Y"]))
                   == B_XY)
        return c4.set_hero_type(scen, rec, "g000uu0019")
    if what == "dummyhurt":
        rec = m17.leader_of(scen, m17.stack_at(scen, fc.dummy(prof)["xy"], neutral["PLAYER_ID"]))
        return scen.with_record(rec, replace(rec, body=sg.unit_body(rec.obj_id, fc.DUMMY.upper(), 1, 1)))
    if what == "header":
        at, n = sg.header_name_offset(scen.header), len(prof["name"].encode(sg.ENC))
        return replace(scen, header=scen.header[:at] + b"x" * n + scen.header[at + n:])
    return mp.sabotage_map(scen, what, t)


FILE_SABOTAGE = [("maxunit", "MAX_UNIT сбит на 2"), ("leadermax", "потолок уровня героя 1"),
                 ("bank", "казна не та"), ("name", "ScenarioInfo.NAME не тот"),
                 ("header", "имя в заголовке не то"), ("noparty", "отряд героя пропал"),
                 ("nobag", "мешок пропал"), ("onebag", "оба IG6001 в мешке героя A"),
                 ("noskill", "герой B не Архимаг"), ("dummyhurt", "мишень ранена")]


def file_cases(template: Path, scratch: Path, t: sq.Tables) -> list[tuple[str, str | None]]:
    scratch.mkdir(parents=True, exist_ok=True)
    out = scratch / "f7_m21_selftest.sg"
    scen, _ = mp.build(template, KEY, t)
    scen.write(out)
    honest = fp.raises(lambda: verify(out, t, quiet=True))
    if honest is not None:
        raise SystemExit("честная карта m21 не проходит собственную проверку: %s" % honest)
    cases = []
    for what, title in FILE_SABOTAGE:
        sabotage(scen, what, t).write(out)
        cases.append(("файл: " + title, fp.raises(lambda: verify(out, t, quiet=True))))
    out.unlink()
    return cases


def _bag(prof: dict, k: int, **kw) -> dict:
    return dict(prof, bags=[dict(b, **kw) if i == k else b for i, b in enumerate(prof["bags"])])


def _dummy(prof: dict, **kw) -> dict:
    return dict(prof, neutrals=[dict(fc.dummy(prof), **kw)])


def _staff(prof: dict, **kw) -> dict:
    return dict(prof, staff=dict(prof["staff"], **kw))


def profile_cases(t: sq.Tables, prof: dict) -> list[tuple[str, str | None]]:
    """Порча ровно одного обещания за раз."""
    r, ok = fp.raises, {"MAX_UNIT": 5, "MAX_SPELL": 5, "MAX_LEADER": 99, "MAX_CITY": 5}
    no_b = dict(prof, parties=[prof["parties"][0], dict(prof["parties"][1], leader="g000uu0019")])
    return [
        ("посохи A и A2 разного типа (IG6003)", r(lambda: check_staff(
            t, _bag(prof, 1, items=["G000IG6003", OTHER]), KEY))),
        ("оба IG6001 у героя A (мешок B рядом с A)", r(lambda: check_staff(
            t, _bag(prof, 1, xy=(13, 13)), KEY))),
        ("мешок B посередине — чей, не ясно", r(lambda: check_staff(t, _bag(prof, 1, xy=(15, 12)), KEY))),
        ("контрольный посох того же типа", r(lambda: check_staff(
            t, _bag(prof, 1, items=[WAND, WAND]), KEY))),
        ("у героя B нет навыка посохов (Пегас)", r(lambda: check_staff(t, no_b, KEY))),
        ("мишень иммунна к земле (Белый дракон)", r(lambda: check_staff(
            t, _dummy(prof, leader="g000uu5020"), KEY))),
        ("мишень вне обзора", r(lambda: check_staff(t, _dummy(prof, xy=(40, 40)), KEY))),
        ("маны жизни не хватит на два IG6001", r(lambda: check_staff(
            t, dict(prof, bank="G1000:R0000:Y0100:E1000:W1000:B0000"), KEY))),
        ("лорд Маг: экземпляр = тип", r(lambda: check_staff(t, _staff(prof, lord=fc.LORD_MAGE), KEY))),
        ("без применения A2 гипотезы сливаются", r(lambda: check_staff(t, _staff(
            prof, timeline=tuple(e for e in TIMELINE if e != ("cast", "A2"))), KEY))),
        ("таблица гипотез пуста", r(lambda: fc.check_distinct({}, "пусто", KEY))),
        ("MAX_UNIT не 5", r(lambda: fp.check_scenario_limits(dict(ok, MAX_UNIT=2), KEY))),
    ]


def selftest(template: Path, scratch: Path) -> int:
    t, prof = sq.Tables(), fp.profile(KEY)
    honest = fp.raises(lambda: (fp.check_profile(t, KEY, prof), check_staff(t, prof, KEY)))
    if honest is not None:
        raise SystemExit("честный профиль m21 не проходит сторожей: %s" % honest)
    groups = [("профиль", profile_cases(t, prof)), ("файл карты", file_cases(template, scratch, t))]
    bad = total = 0
    for title, cases in groups:
        if not cases:
            raise SystemExit("группа «%s» не сделала ни одного сравнения — это не проверка" % title)
        print("--- %s: %d сравнений" % (title, len(cases)))
        for name, msg in cases:
            print("%-44s %s" % (name, ("ловится: " + msg.split("\n")[0]) if msg else "ПРОПУЩЕН"))
            bad += msg is None
        total += len(cases)
    print("selftest m21: сработали %d сторожей из %d сравнений" % (total - bad, total))
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
    if a.selftest:
        return selftest(a.template, a.scratch)
    t, prof = sq.Tables(), fp.profile(KEY)
    fp.check_profile(t, KEY, prof)
    print_plan(t, prof, check_staff(t, prof, KEY))
    if a.plan:
        return 0
    scen, stats = mp.build(a.template, KEY, t)
    out = a.out_dir / prof["file"]
    scen.write(out)
    print("%s: %s" % (out, ", ".join("%s %s" % kv for kv in stats.items())))
    verify(out, t)
    print("в %s: %s" % (a.out_dir, ", ".join(sorted(x.name for x in a.out_dir.glob("*.sg")))))
    return 0


if __name__ == "__main__":
    sys.exit(main())
