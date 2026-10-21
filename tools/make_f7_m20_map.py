#!/usr/bin/env python3
"""Карта M20 `f7-m20-capture-mana` — переходит ли МАНА побеждённого при захвате столицы.

    python tools/make_f7_m20_map.py             # записать game-dev/Exports/f7_m20_capture_mana.sg
    python tools/make_f7_m20_map.py --plan      # бой со стражем и таблица гипотез, без записи
    python tools/make_f7_m20_map.py --selftest  # порча файла плюс сторож

Заплата поверх эталона `2-capital-test.sg` (общая часть — `tools/f7_twocap.py`).

**Что известно.** Вторая большая партия (`big2-analysis.md` §8.1): при захвате столицы
**золото** побеждённого уходит захватчику с обрезкой по 9999 (Нежить 379 → 0, человек
4527 → 4906; Империя 6662 → 0, человек 5523 → 9999). Мана не видна: у человека её было 9999.

**Карта.** Казна игрока — нули по всем шести ресурсам, казна соседа — узнаваемые числа
`G0500:R0333:Y0444:E0555:W0666:B0777`. Суммы далеко от 9999, поэтому три гипотезы — «всё с
обрезкой», «только золото», «ничего» — расходятся на каждом виде маны.

**Гибель игрока.** Условие в коде не дочитано (сообщение «Вашу столицу захватили» — `push
0x6D4AD4` в `0x491436`, выбор строки — `0x4025EE`). Данные: Империя и Нежить погибли в момент
захвата столицы, не имея ни одного города (`t20-022`, `t21-023`: города только у нейтралов и
человека), а два города, взятые потом осиротевшими отрядами Империи, её не оживили (`t25-028`:
города у `PL0009`, дохода нет). Поэтому здесь у соседа **нет ни городов, ни отрядов на карте** —
при любом из разумных условий («нет столицы», «нет укреплений», «нет войск») захват его убивает.

**Бой.** В столице, кроме гарнизона из файла (King's Guard), выходит страж расы (900 HP,
броня 50, 250 урона по всему строю, инициатива 90) — см. шапку `f7_twocap.py`. Наш отряд — три
Друллиана (`g000uu8103` лидер, `g000uu8003` солдаты: 2000 HP, броня 50, 175 урона по всем) —
стоит в шаге от входа столицы и берёт её первым же ходом, до хода ИИ. Лорд любой.
Игра отсюда не запускается.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402
import scenario_c4 as sc  # noqa: E402
import scenario_squads as sq  # noqa: E402
import make_c4_map as c4  # noqa: E402
import make_city_map as city  # noqa: E402
import make_f7_patch_maps as mp  # noqa: E402
import make_f7_m15_map as m15  # noqa: E402
import f7_plan as fp  # noqa: E402
import f7_twocap as tw  # noqa: E402

KEY = "m20"
LEADER, SOLDIER = "g000uu8103", "g000uu8003"   # Друллиан: лидер (LEADERSHIP 6) и солдат
PARTY_XY = (31, 31)                             # шаг наружу от входа столицы соседа (30,30)
NEIGHBOR_CAPITAL = (26, 26)
RESOURCES = ("G золото", "R преисподняя", "Y жизнь", "E смерть", "W руны", "B роща")

PROFILE: dict = {
    "key": KEY, "file": "f7_m20_capture_mana.sg", "name": "f7-m20-capture-mana",
    "bank": "G0000:R0000:Y0000:E0000:W0000:B0000",
    "neighbor_bank": "G0500:R0333:Y0444:E0555:W0666:B0777",
    "reach_turns": 1,
    "parties": [tw.party(PARTY_XY, LEADER, (SOLDIER, SOLDIER), cell=1,
                         why="захватчик: три Друллиана в шаге от входа", exempt=("capital",))],
}


def build(t: sq.Tables, src: Path = tw.SRC, prof: dict = PROFILE) -> sg.Sg:
    scen = tw.base(src, prof)
    human, neighbor, _ = tw.players(scen)
    scen = mp.set_bank(scen, neighbor["PLAYER_ID"], prof["neighbor_bank"])
    scen = tw.add_parties(scen, prof["parties"], human["PLAYER_ID"], t)
    return tw.finish(scen, prof)


# ------------------------------------------------------------------ ожидания

def expectation_rows(scen: sg.Sg) -> list[dict]:
    """По ресурсу: что придёт при трёх гипотезах. Игроку к захвату — казна плюс один дневной
    тик (он идёт при загрузке); соседу — казна без тика и с тиком (ход ИИ ещё не наступал,
    но тик мог пройти при загрузке и у него). Надбавку дохода за сложность не знаем — поэтому
    в памятке главная формула «казна до боя + казна соседа», а числа — при нулевой надбавке."""
    human, neighbor, _ = tw.players(scen)
    h = [a + b for a, b in zip(tw.bank_parts(human["BANK"]),
                               tw.bank_parts(fp.grace(sc.RACE_EMPIRE)["INCOME"]))]
    n0 = tw.bank_parts(neighbor["BANK"])
    n1 = [a + b for a, b in zip(n0, tw.bank_parts(fp.grace(tw.NEIGHBOR_RACE)["INCOME"]))]
    rows = []
    for k, name in enumerate(RESOURCES):
        rows.append({"ресурс": name, "игрок": h[k], "сосед": n0[k], "сосед+тик": n1[k],
                     "всё": min(fp.GOLD_CAP, h[k] + n0[k]),
                     "всё+тик": min(fp.GOLD_CAP, h[k] + n1[k]),
                     "сырое": h[k] + n1[k],
                     "только золото": h[k] + n0[k] if k == 0 else h[k],
                     "только золото+тик": h[k] + n1[k] if k == 0 else h[k], "ничего": h[k]})
    return rows


# ------------------------------------------------------------------------ сторожа

def check_banks(ch: tw.Pairs, scen: sg.Sg, prof: dict) -> None:
    """Сначала узнаваемость по числам файла, потом точное совпадение с профилем."""
    _, neighbor, _ = tw.players(scen)
    vals = tw.bank_parts(neighbor["BANK"])
    ch.eq("казна соседа: шесть различных чисел", len(set(vals)), 6)
    ch.is_true("казна соседа: мана не нулевая", all(v > 0 for v in vals[1:]), str(vals))
    for r in expectation_rows(scen):
        ch.is_true("%s: сумма ниже 9999 (без обрезки)" % r["ресурс"],
                   r["сырое"] < fp.GOLD_CAP, str(r["сырое"]))
        ch.is_true("%s: «всё» и «ничего» различаются" % r["ресурс"], r["всё"] != r["ничего"])
    ch.eq("казна соседа", neighbor["BANK"], prof["neighbor_bank"])


def check_neighbor(ch: tw.Pairs, scen: sg.Sg) -> None:
    _, neighbor, _ = tw.players(scen)
    pid = neighbor["PLAYER_ID"]
    el = city._has_p_o_el(scen)
    towns = [r for r in scen.find(city.WHAT_VILLAGE)
             if city.parse_village(r.body, el)["OWNER"] == pid]
    ch.eq("городов у соседа (иначе захват столицы может его не убить)", len(towns), 0)
    ch.eq("отрядов соседа на карте", len(tw.field_stacks(scen, pid)), 0)
    ch.eq("столиц у соседа", sum(sg.fields(r.body, sg.LAYOUT_CAPITAL)["OWNER"] == pid
                                 for r in scen.find(sg.WHAT_CAPITAL)), 1)
    ch.eq("столица соседа на месте", tw.capital_xy(scen, pid), NEIGHBOR_CAPITAL)


def defenders(t: sq.Tables, scen: sg.Sg) -> list[str]:
    """Страж расы (в файле его нет, в бой выходит) плюс гарнизон столицы из файла."""
    _, neighbor, _ = tw.players(scen)
    sid = sg.fields(tw.capital_of(scen, neighbor["PLAYER_ID"]).body, sg.LAYOUT_CAPITAL)["STACK"]
    rec = next((r for r in scen.find(sg.WHAT_STACK) if r.obj_id == sid), None)
    garrison = [] if rec is None else [u for u, _, _ in
                                       tw.stack_units(t, scen, sg.parse_stack(rec.body))]
    return [fp.grace(tw.NEIGHBOR_RACE)["GUARDIAN"].lower()] + garrison


def check_party(ch: tw.Pairs, t: sq.Tables, scen: sg.Sg, prof: dict) -> dict | None:
    human, neighbor, _ = tw.players(scen)
    mine = tw.field_stacks(scen, human["PLAYER_ID"])
    ch.eq("наших отрядов на карте", len(mine), 1)
    if not mine:
        return None
    _, s = mine[0]
    e = tw.entrance(tw.capital_xy(scen, neighbor["PLAYER_ID"]), tw.CAPITAL_SIZE)
    lead = t.unit(mp.unit_types(scen)[s["LEADER_ID"].upper()])
    need = m15.turns_to_walk(int(lead["MOVE"]), max(1, fp.cheb(tw.xy_of(s), e)))
    ch.is_true("до входа столицы %s за %d ход" % (e, prof["reach_turns"]),
               need <= prof["reach_turns"], "нужно ходов %d" % need)
    ours = tw.stack_units(t, scen, s)
    ch.is_true("солдат не больше лидерства", len(ours) - 1 <= int(lead["LEADERSHIP"] or 0))
    return tw.check_battle(ch, t, "захват столицы", ours, defenders(t, scen),
                           tw.capital_floor(tw.NEIGHBOR_RACE))


def verify(path: Path, t: sq.Tables, quiet: bool = False, prof: dict = PROFILE) -> None:
    scen = sg.Sg.read(path)
    ch = tw.Pairs("проверка чтением %s" % prof["file"])
    check_banks(ch, scen, prof)
    tw.check_common(ch, t, scen, prof)
    tw.check_garrisons(ch, scen, KEY)
    check_neighbor(ch, scen)
    check_party(ch, t, scen, prof)
    tw.check_safety(ch, t, scen, prof)
    ch.done(quiet)


# ------------------------------------------------------------------------ печать

def print_plan(t: sq.Tables, scen: sg.Sg, prof: dict = PROFILE) -> None:
    human, neighbor, _ = tw.players(scen)
    print("%s -> %s: мана при захвате столицы; казна игрока %s, соседа %s"
          % (KEY, prof["name"], human["BANK"], neighbor["BANK"]))
    s = tw.field_stacks(scen, human["PLAYER_ID"])[0][1]
    foes = defenders(t, scen)
    floor = tw.capital_floor(tw.NEIGHBOR_RACE)
    b = tw.battle(t, tw.stack_units(t, scen, s), foes, floor)
    print("   отряд %s: %s; защитники %s, пол брони укрепления %d"
          % (tw.xy_of(s), [t.name(u) for u, _, _ in tw.stack_units(t, scen, s)],
             [t.name(u) for u in foes], floor))
    print("   бой: раундов до победы %s, урон по защитникам за раунд %s, худший входящий %s"
          % (b["rounds"], {t.name(k): round(v) for k, v in b["damage"].items()}, b["incoming"]))
    print("   %-15s %6s %6s %6s | %11s %11s | %11s %11s | %6s"
          % ("ресурс", "игрок", "сосед", "+тик", "всё", "всё+тик", "золото", "золото+тик",
             "ничего"))
    for r in expectation_rows(scen):
        print("   %-15s %6d %6d %6d | %11d %11d | %11d %11d | %6d"
              % (r["ресурс"], r["игрок"], r["сосед"], r["сосед+тик"], r["всё"], r["всё+тик"],
                 r["только золото"], r["только золото+тик"], r["ничего"]))


# ---------------------------------------------------------------------- selftest

def sabotage(scen: sg.Sg, what: str, t: sq.Tables) -> sg.Sg:
    human, neighbor, _ = tw.players(scen)
    pid = neighbor["PLAYER_ID"]
    banks = {"humanbank": (human["PLAYER_ID"], "G5000:R0000:Y0000:E0000:W0000:B0000"),
             "dupbank": (pid, "G0500:R0444:Y0444:E0555:W0666:B0777"),
             "zeromana": (pid, "G0500:R0333:Y0444:E0000:W0666:B0777"),
             "clip": (pid, "G0500:R0333:Y9990:E0555:W0666:B0777")}
    if what in banks:
        return mp.set_bank(scen, *banks[what])
    if what == "village":
        sub = str(sg.fields(tw.capital_of(scen, pid).body, sg.LAYOUT_CAPITAL)["SUBRACE"])
        scen = city.add_village(scen, pid, sub, (40, 4), 1, "Город соседа", [])[0]
        return tw.rebuild_plan(city.set_qty_cities(scen, 1))
    if what == "fieldstack":
        return tw.rebuild_plan(tw.add_parties(
            scen, [tw.party((44, 4), fp.grace(tw.NEIGHBOR_RACE)["LEADER_1"].lower())], pid, t))
    if what == "strongguard":                # Небирос в гарнизоне: 2600 HP, 300 урона по всем
        sid = sg.fields(tw.capital_of(scen, pid).body, sg.LAYOUT_CAPITAL)["STACK"]
        return c4.set_hero_type(scen, next(r for r in scen.find(sg.WHAT_STACK)
                                           if r.obj_id == sid), "g000uu8001")
    if what == "far":
        rec, _ = tw.field_stacks(scen, human["PLAYER_ID"])[0]
        return tw.rebuild_plan(tw.patch_stack(scen, rec, POS_X=0, POS_Y=0))
    if what == "nocapstack":
        return mp.sabotage_map(scen, what, t)
    return tw.common_sabotage(scen, what, PROFILE, t)


SABOTAGE = tw.COMMON_SABOTAGE + [
    ("nocapstack", "у чужой столицы пустой гарнизон"),
    ("humanbank", "казна игрока не та"), ("dupbank", "две маны соседа совпали"),
    ("zeromana", "у соседа нулевая мана"), ("clip", "сумма упрётся в 9999"),
    ("village", "у соседа есть город"), ("fieldstack", "у соседа есть отряд на карте"),
    ("strongguard", "гарнизон столицы не по зубам"), ("far", "отряд за ход не дойдёт")]


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
        return tw.selftest(PROFILE, lambda t: build(t, a.src), verify, sabotage, SABOTAGE,
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
