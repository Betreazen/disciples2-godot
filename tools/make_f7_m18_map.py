#!/usr/bin/env python3
"""Карта M18 `f7-m18-morale` — сопротивление ГОРОДА действию вора (`GVars.MORALE_n`).

    python tools/make_f7_m18_map.py             # записать game-dev/Exports/f7_m18_morale.sg
    python tools/make_f7_m18_map.py --plan      # раскладка и таблица ожиданий, без записи
    python tools/make_f7_m18_map.py --selftest  # порча файла плюс сторож

Заплата поверх эталона `2-capital-test.sg` (общая часть — `tools/f7_twocap.py`).

**Что меряем.** Порог `0x5D31A1`: при `CHANCE < 100` и цели-укреплении (`0x1D`) из шанса
вычитается `MORALE[уровень]`, где уровень — `CMidVillage.tierLevel` (`+0x90`), а у столицы
`dynamic_cast` к `CMidVillage` даёт ноль и индекс остаётся 6. **Владельца функция не читает
вовсе** — поэтому вычет у нейтрального города тот же, что у чужого (ранг 1), и пять городов
здесь нейтральные: они не нанимают и к ворам не выходят.

**Что покажет окно — список `0x5D3856`, прочитан до `ret` (ранг 1 по адресам, смысл
вспомогательных функций — ранг 2).**

* ранний выход, если в деревне идёт бунт (`0x5D37E8`, сравнение с `+0x98`) — `RIOT_T = 0`;
* `L_RIOT_CITY` — только лорду `L_DIPLOMAT`;
* **если внутри укрепления стоит отряд (`+0x8C`, поле `STACK`) — дальше ничего** (`0x5D38D8`);
* `L_POISON_CITY` — если в гарнизоне есть юнит с HP > 1 (`0x5D371F`): по Гоблину в каждом;
* владелец не играбельный (`0x5E5358`) — только `L_BRIBE` и конец. **Против нейтрального
  города из действий с `CHANCE < 100` в окне ровно одно: Poison City (90).**
* владелец играбельный — `L_SPY`, и лорду `L_DIPLOMAT`: `L_STEAL_GOLD` (80), `L_STEAL_SPELL`
  (80, если у цели есть неизвестные нам заклинания — у соседа их ноль), `L_ASSASSINATE` (70,
  только `CMidVillage`, у столицы нет), `L_MISFIT` (90).

**Отступление от общего сторожа, названное вслух: столица соседа БЕЗ отряда внутри.** Его
единственный отряд (`KC0001`, King's Guard) выведен на карту в дальний угол: с отрядом внутри
окно против столицы пустое (`0x5D38D8`), и строка `MORALE_6` не измерилась бы. Такое
состояние штатное — в живом сохранении M15 `t04-028` у ОБЕИХ столиц `STACK` пуст, а игра шла
дальше. Раса соседа объявлена в обоих ростерах, и отряд у него есть — это сторожа проверяют.

Играть **Повелителем Воров**: гейт `L_DIPLOMAT` (`0x5D3A89`, `0x5D3359`) открывает кражу и
подложный приказ; под другим лордом против столицы останется Spy (100) и Poison City.
Игра отсюда не запускается.
"""
from __future__ import annotations

import argparse
import struct
import sys
from dataclasses import replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402
import scenario_squads as sq  # noqa: E402
import make_city_map as city  # noqa: E402
import make_f7_maps as mf  # noqa: E402
import make_f7_patch_maps as mp  # noqa: E402
import make_f7_m15_map as m15  # noqa: E402
import f7_plan as fp  # noqa: E402
import f7_twocap as tw  # noqa: E402

KEY = "m18"
VILLAGES = [(1, (20, 1)), (2, (32, 1)), (3, (42, 8)), (4, (1, 20)), (5, (1, 34))]
NEIGHBOR_CAPITAL = (26, 26)     # столица соседа в эталоне; сторож сверяет с файлом
EVICT_XY = (44, 44)             # куда выведен отряд соседа: дальний угол, воры вне его хода
CAT_NAME = {3: "Assassinate", 4: "Counterfeit Orders", 6: "Poison City", 7: "Steal Spell",
            9: "Steal Gold"}
VILLAGE_ACTIONS = (6,)          # нейтральный город: из CHANCE < 100 в окне только Poison City
CAPITAL_ACTIONS = (9, 4, 6)     # столица соседа; Poison City — если в её группе есть юнит с HP > 1


def _thief(xy: tuple[int, int], size: int, title: str, exempt: tuple[str, ...] = ()) -> dict:
    """Вор по диагонали наружу от входа — шаг в одну клетку, как у отрядов M16."""
    e = tw.entrance(xy, size)
    return tw.party((e[0] + 1, e[1] + 1), fp.HERO_THIEF, why="вор у %s, вход %s" % (title, e),
                    exempt=exempt)


PROFILE: dict = {
    "key": KEY, "file": "f7_m18_morale.sg", "name": "f7-m18-morale",
    "lord": "G000LR0002",
    "bank": "G1000:R0000:Y0000:E0000:W0000:B0000",
    "villages": [{"tier": n, "xy": xy, "name": "Город %d уровня" % n, "garrison": (fp.GOBLIN,)}
                 for n, xy in VILLAGES],
    "parties": [_thief(xy, city.VILLAGE_TILES, "города %d" % n) for n, xy in VILLAGES]
               + [_thief(NEIGHBOR_CAPITAL, tw.CAPITAL_SIZE, "столицы соседа",
                         exempt=("capital",))],
}


# ------------------------------------------------------------------------ сборка

def evict(scen: sg.Sg, player_id: str, xy: tuple[int, int]) -> sg.Sg:
    """Отряд из столицы — на карту: у отряда пустой `INSIDE`, у столицы пустой `STACK`."""
    cap = tw.capital_of(scen, player_id)
    sid = sg.fields(cap.body, sg.LAYOUT_CAPITAL)["STACK"]
    rec = next(r for r in scen.find(sg.WHAT_STACK) if r.obj_id == sid)
    scen = tw.patch_stack(scen, rec, POS_X=xy[0], POS_Y=xy[1], INSIDE=sg.NULL_ID)
    return tw.patch_capital(scen, tw.capital_of(scen, player_id), STACK=sg.NULL_ID)


def build(t: sq.Tables, src: Path = tw.SRC, prof: dict = PROFILE) -> sg.Sg:
    scen = tw.base(src, prof)
    human, neighbor, neutral = tw.players(scen)
    sub = city.neutral_subrace(scen, neutral["PLAYER_ID"], city.NEUTRAL_SUBRACE)
    for v in prof["villages"]:
        scen, garrison = mf.add_units(scen, tuple(v["garrison"]))
        scen = city.add_village(scen, neutral["PLAYER_ID"], sub, v["xy"], v["tier"], v["name"],
                                garrison)[0]
    scen = city.set_qty_cities(scen, len(prof["villages"]))
    scen = evict(scen, neighbor["PLAYER_ID"], EVICT_XY)
    scen = tw.add_parties(scen, prof["parties"], human["PLAYER_ID"], t)
    return tw.finish(scen, prof)


# ------------------------------------------------------------------ чтение файла

def forts(scen: sg.Sg) -> list[dict]:
    """Пять городов и столица соседа — как они лежат в файле, а не как задуманы."""
    _, neighbor, _ = tw.players(scen)
    el = city._has_p_o_el(scen)
    out = []
    for r in scen.find(city.WHAT_VILLAGE):
        v = city.parse_village(r.body, el)
        xy = (int(v["POS_X"]), int(v["POS_Y"]))
        out.append({"title": "город %d" % int(v["SIZE"]), "tier": int(v["SIZE"]), "xy": xy,
                    "size": city.VILLAGE_TILES, "actions": VILLAGE_ACTIONS, "rec": r, "v": v})
    xy = tw.capital_xy(scen, neighbor["PLAYER_ID"])
    out.append({"title": "столица соседа", "tier": None, "xy": xy, "size": tw.CAPITAL_SIZE,
                "actions": CAPITAL_ACTIONS, "rec": tw.capital_of(scen, neighbor["PLAYER_ID"])})
    return sorted(out, key=lambda f: (f["tier"] is None, f["tier"] or 0))


def expectation_rows(forts_: list[dict]) -> list[dict]:
    """Шанс в окне по трём гипотезам: вычет `MORALE_n` (код), вычет `PROT_n`, без вычета."""
    g = fp.gvars()
    rows = []
    for f in forts_:
        morale = int(g["MORALE_%d" % (f["tier"] or 6)])
        prot = int(g["PROT_%d" % f["tier"]]) if f["tier"] else tw.capital_floor(tw.NEIGHBOR_RACE)
        for cat in f["actions"]:
            base_chance = fp.action_chance(cat)
            rows.append({"укрепление": f["title"], "действие": CAT_NAME[cat], "CHANCE": base_chance,
                         "MORALE": max(0, base_chance - morale), "PROT": max(0, base_chance - prot),
                         "без вычета": base_chance, "вычет": morale,
                         "P(успех)": fp.tri_chance(max(0, base_chance - morale))})
    return rows


# ------------------------------------------------------------------------ сторожа

def check_villages(ch: tw.Pairs, scen: sg.Sg) -> None:
    _, _, neutral = tw.players(scen)
    towns = [f for f in forts(scen) if f["tier"]]
    ch.eq("городов на карте", len(towns), 5)
    ch.eq("QTY_CITIES", int(city.info_fields(scen)["QTY_CITIES"]), len(towns))
    ch.eq("уровни городов", sorted(f["tier"] for f in towns), [1, 2, 3, 4, 5])
    units = {r.obj_id.upper(): r for r in scen.find(sg.WHAT_UNIT)}
    _, plan = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    for f in towns:
        v, title = f["v"], f["title"]
        ch.eq("%s: владелец нейтральный" % title, v["OWNER"], neutral["PLAYER_ID"])
        ch.eq("%s: бунта нет (0x5D37E8 закрыл бы окно)" % title, int(v["RIOT_T"]), 0)
        ch.eq("%s: отряда внутри нет (0x5D38D8)" % title, v["STACK"], sg.NULL_ID)
        hps = [mp.unit_hp(units[v["UNIT_%d" % k].upper()]) for k in range(6)
               if v["UNIT_%d" % k] != sg.NULL_ID]
        ch.is_true("%s: в гарнизоне юнит с HP > 1 (0x5D371F, иначе Poison City не покажут)"
                   % title, any(hp > 1 for hp in hps), str(hps))
        ch.eq("%s: клеток в плане" % title,
              sum(1 for _, _, e in plan if e == f["rec"].obj_id), city.VILLAGE_TILES ** 2)


def check_capital(ch: tw.Pairs, scen: sg.Sg) -> None:
    human, neighbor, _ = tw.players(scen)
    ch.eq("столица соседа там же, где в эталоне", tw.capital_xy(scen, neighbor["PLAYER_ID"]),
          NEIGHBOR_CAPITAL)
    cap = sg.fields(tw.capital_of(scen, neighbor["PLAYER_ID"]).body, sg.LAYOUT_CAPITAL)
    ch.eq("столица соседа без отряда внутри (0x5D38D8)", cap["STACK"], sg.NULL_ID)
    ch.is_true("у соседа есть отряд на карте",
               bool(tw.field_stacks(scen, neighbor["PLAYER_ID"])))
    mine = sg.fields(tw.capital_of(scen, human["PLAYER_ID"]).body, sg.LAYOUT_CAPITAL)
    ch.is_true("у своей столицы гарнизон на месте", mine["STACK"] != sg.NULL_ID)


def check_thieves(ch: tw.Pairs, t: sq.Tables, scen: sg.Sg) -> None:
    """У каждого укрепления вор ровно в шаге от входа, и шаг оплачен его очками хода."""
    human, _, _ = tw.players(scen)
    all_forts = forts(scen)
    ch.eq("наших отрядов на карте", len(tw.field_stacks(scen, human["PLAYER_ID"])),
          len(all_forts))
    types = mp.unit_types(scen)
    for f in all_forts:
        e = tw.entrance(f["xy"], f["size"])
        want = (e[0] + 1, e[1] + 1)
        got = tw.stack_at(scen, human["PLAYER_ID"], want)
        ch.is_true("вор у %s в %s" % (f["title"], want), got is not None,
                   "в клетке пусто, вход %s" % (e,))
        if got is None:
            continue
        _, s = got
        ch.eq("%s: лидер — вор (LEADER_CAT)" % f["title"], tw.leader_cat(t, scen, s),
              fp.CAT_THIEF)
        move = int(t.unit(types[s["LEADER_ID"].upper()])["MOVE"])
        ch.is_true("%s: шаг до входа оплачен" % f["title"],
                   fp.cheb(want, e) * fp.PLAIN_COST <= move, "MOVE %d" % move)


def check_expectations(ch: tw.Pairs, scen: sg.Sg) -> None:
    rows = expectation_rows(forts(scen))
    ch.is_true("таблица ожиданий непуста", bool(rows))
    ch.is_true("у всех действий таблицы CHANCE < 100 (иначе вычета нет, 0x5D31D0)",
               all(r["CHANCE"] < 100 for r in rows))
    ch.is_true("ни одно ожидание не упёрлось в ноль", all(r["MORALE"] > 0 for r in rows))
    ch.is_true("хотя бы строка различает MORALE и PROT",
               any(r["MORALE"] != r["PROT"] for r in rows))
    ch.eq("строк на столице MORALE_6", sum(r["укрепление"] == "столица соседа" for r in rows),
          len(CAPITAL_ACTIONS))


def verify(path: Path, t: sq.Tables, quiet: bool = False, prof: dict = PROFILE) -> None:
    scen = sg.Sg.read(path)
    ch = tw.Pairs("проверка чтением %s" % prof["file"])
    tw.check_common(ch, t, scen, prof)
    check_villages(ch, scen)
    check_capital(ch, scen)
    check_thieves(ch, t, scen)
    tw.check_safety(ch, t, scen, prof)
    check_expectations(ch, scen)
    ch.done(quiet)


# ------------------------------------------------------------------------ печать

def print_plan(t: sq.Tables, scen: sg.Sg, prof: dict = PROFILE) -> None:
    human, neighbor, _ = tw.players(scen)
    cover, who = m15.enemy_cover(t, scen, {"race": tw.NEIGHBOR_RACE, "turns": tw.AI_TURNS})
    print("%s -> %s: сопротивление города MORALE_n, лорд %s, казна %s"
          % (KEY, prof["name"], prof["lord"], prof["bank"]))
    for f in forts(scen):
        e = tw.entrance(f["xy"], f["size"])
        print("   %-15s пятно %s %dx%d, вход %s, вор %s" % (f["title"], f["xy"], f["size"],
                                                          f["size"], e, (e[0] + 1, e[1] + 1)))
    print("   отряд соседа выведен в %s; угроза соседа за %d ход: %d клеток (%s)"
          % (EVICT_XY, tw.AI_TURNS, cover, who))
    print("   %-15s %-19s %6s %6s %6s %6s %9s" % ("укрепление", "действие", "CHANCE", "MORALE",
                                               "PROT", "без", "P(успех)"))
    for r in expectation_rows(forts(scen)):
        print("   %-15s %-19s %6d %6d %6d %6d %9.3f" % (r["укрепление"], r["действие"],
                                                     r["CHANCE"], r["MORALE"], r["PROT"],
                                                     r["без вычета"], r["P(успех)"]))


# ---------------------------------------------------------------------- selftest

def _drop(scen: sg.Sg, obj_id: str) -> sg.Sg:
    return tw.rebuild_plan(replace(scen, records=tuple(r for r in scen.records
                                                       if r.obj_id != obj_id)))


def _thief_rec(scen: sg.Sg) -> tuple[sg.Record, dict]:
    """Вор города 1 уровня — на нём и портим отряды."""
    human, _, _ = tw.players(scen)
    e = tw.entrance(VILLAGES[0][1], city.VILLAGE_TILES)
    return tw.stack_at(scen, human["PLAYER_ID"], (e[0] + 1, e[1] + 1))


def sabotage(scen: sg.Sg, what: str, t: sq.Tables) -> sg.Sg:
    human, neighbor, _ = tw.players(scen)
    evicted = tw.field_stacks(scen, neighbor["PLAYER_ID"])[0][0]
    thief, ts = _thief_rec(scen)
    if what == "lord":
        return m15.set_lord(scen, human["PLAYER_ID"], "G000LR0001")
    if what in ("tier", "nogarrison"):
        return mp.sabotage_map(scen, what, t)
    if what in ("owner", "riot"):
        rec = scen.find(city.WHAT_VILLAGE)[0]
        owner = city.parse_village(rec.body, city._has_p_o_el(scen))["OWNER"]
        body = (rec.body[:-4] + struct.pack("<i", 2) if what == "riot"   # RIOT_T — последнее поле
                else rec.body.replace(owner.encode(), neighbor["PLAYER_ID"].encode(), 1))
        return scen.with_record(rec, replace(rec, body=body))
    if what == "capstack":
        cap = tw.capital_of(scen, neighbor["PLAYER_ID"])
        scen = tw.patch_stack(scen, evicted, POS_X=NEIGHBOR_CAPITAL[0], POS_Y=NEIGHBOR_CAPITAL[1],
                              INSIDE=cap.obj_id)
        scen = tw.patch_capital(scen, tw.capital_of(scen, neighbor["PLAYER_ID"]),
                                STACK=evicted.obj_id)
        return tw.rebuild_plan(scen)
    if what == "nothief":
        return _drop(scen, thief.obj_id)
    if what == "noevicted":
        return _drop(scen, evicted.obj_id)
    x, y = tw.xy_of(ts)
    if what == "thieffar":
        return tw.rebuild_plan(tw.patch_stack(scen, thief, POS_X=x + 2))
    if what == "danger":
        return tw.rebuild_plan(tw.patch_stack(scen, evicted, POS_X=x + 3, POS_Y=y))
    raise SystemExit("нет порчи %s" % what)


SABOTAGE = tw.COMMON_SABOTAGE + [
    ("lord", "лорд не Повелитель Воров"), ("tier", "у города сбит уровень"),
    ("owner", "город отдан соседу"), ("nogarrison", "гарнизон города пуст"),
    ("riot", "в городе бунт"), ("capstack", "отряд соседа вернулся в столицу"),
    ("nothief", "вора у города нет"), ("thieffar", "вор отведён от входа"),
    ("danger", "отряд соседа в шаге от вора"), ("noevicted", "у соседа не осталось отряда")]


def sabotage_any(scen: sg.Sg, what: str, t: sq.Tables) -> sg.Sg:
    if what in dict(tw.COMMON_SABOTAGE):
        return tw.common_sabotage(scen, what, PROFILE, t)
    return sabotage(scen, what, t)


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--src", type=Path, default=tw.SRC, help="эталон пользователя, только чтение")
    p.add_argument("--out-dir", type=Path, default=mp.EXPORTS)
    p.add_argument("--plan", action="store_true", help="только расчёт, ничего не записывать")
    p.add_argument("--selftest", action="store_true", help="порча файла плюс сторож")
    p.add_argument("--scratch", type=Path,
                   default=Path(__file__).resolve().parents[1] / "corpus" / "_f7-selftest")
    a = p.parse_args(argv)
    if not a.src.exists():
        raise SystemExit("нет эталона %s" % a.src)
    if a.selftest:
        return tw.selftest(PROFILE, lambda t: build(t, a.src), verify, sabotage_any, SABOTAGE,
                           a.scratch)
    t = sq.Tables()
    scen = build(t, a.src)
    print_plan(t, scen)
    if a.plan:
        return 0
    out = a.out_dir / PROFILE["file"]
    scen.write(out)
    print("%s: записей %d" % (out, len(scen.records)))
    verify(out, t)
    return 0


if __name__ == "__main__":
    sys.exit(main())
