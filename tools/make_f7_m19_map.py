#!/usr/bin/env python3
"""Карта M19 `f7-m19-diplo2` — атака при мире без союза и снос ЧУЖОГО жезла.

    python tools/make_f7_m19_map.py             # записать game-dev/Exports/f7_m19_diplo2.sg
    python tools/make_f7_m19_map.py --plan      # раскладка, бой и таблица гипотез, без записи
    python tools/make_f7_m19_map.py --selftest  # порча файла плюс сторож

Заплата поверх эталона `2-capital-test.sg` (общая часть — `tools/f7_twocap.py`).

**Вопрос 1 — атака при мире без союза.** Прогон 4 дал 75 → 66, и из 75 не различить
присваивание `D_ATTAK_PC = 66` (`0x5D97C3…0x5D97CB`, `absolute = 1`) и вычет 9. Старт здесь
**90**: присваивание даёт 66, вычет — 81. Мишень — отряд соседа в шаге от нашего; атака в
первый же ход, до хода ИИ (при `relation ≥ 90` ИИ на своём ходу предлагает союз с шансом 100,
`0x448AAD`).

**Вопрос 2 — `D_ROD = −15` за снос чужого жезла** (`CEffectBreakRod::apply 0x433C65` →
`0x5D9FAB` → `CVisitorDestroyRod 0x5E873D`). Живьём не наблюдался: за все прогоны число
`CMidRod` ни разу не уменьшалось. Жезл соседа ставится записью `CMidRod` (раскладка снята с
сохранений M15: `ROD_ID`, `OWNER`, `POS_X`, `POS_Y`, клеткой в плане). Рядом — наш жезлоносец;
снос — его отдельная команда, съедает остаток хода (`run2-analysis.md` §2.2).

**Разрешён ли снос чужого жезла при мире без союза — да, ранг 1 по гейту.** Команда по объекту
`0x4D0CD0…0x4D0D80`: для цели-жезла (`0x2B`) единственная проверка — `arePlayersAllied
0x5F7EBD` (владелец жезла `+0x1C` против владельца отряда `+0x8C`), отказ — строка
`X005TA0303`. Отношение само по себе не проверяется; `CVisitorDestroyRod::canApply 0x613A8B`
владельца не спрашивает. Союза на карте нет — бит 0 слова `RELATION` сторож держит нулём.

**Третья сторона** (`0x5D94F7…0x5D9549`): каждой расе X, воюющей с B, достаётся
`rel(A, X) − value/2`. Нейтралы воюют с Кланами (пара 4-3 = 0), значит у пары нейтралы —
Империя тоже будет след, и он тоже различает гипотезы. Таблица — `--plan`.

Этот прогон пойдёт после хука зонда на `changeRelations 0x5D9366` — карта от этого не
меняется. Лорд любой. Игра отсюда не запускается.
"""
from __future__ import annotations

import argparse
import sys
from dataclasses import replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402
import scenario_c4 as sc  # noqa: E402
import scenario_squads as sq  # noqa: E402
import make_c4_map as c4  # noqa: E402
import make_f7_patch_maps as mp  # noqa: E402
import make_f7_m15_map as m15  # noqa: E402
import sg_second_player as sp  # noqa: E402
import f7_plan as fp  # noqa: E402
import f7_twocap as tw  # noqa: E402

KEY = "m19"
ANGEL = "g000uu0014"            # 225 HP, 125 урона в ближнем бою
ATTACKER_XY, FOE_XY = (20, 4), (21, 4)
BEARER_XY, ROD_XY = (35, 4), (36, 4)
NEB = {"race": tw.NEIGHBOR_RACE}

PROFILE: dict = {
    "key": KEY, "file": "f7_m19_diplo2.sg", "name": "f7-m19-diplo2",
    "bank": "G1000:R0000:Y0000:E0000:W0000:B0000",
    "relation": 90,
    "foe": {"xy": FOE_XY, "leader": fp.grace(tw.NEIGHBOR_RACE)["LEADER_1"].lower()},
    "rod": ROD_XY,
    "parties": [
        tw.party(ATTACKER_XY, fp.HERO_FIGHTER, (ANGEL, ANGEL, ANGEL), cell=1,
                 why="атакующий: три Ангела в переднем ряду, лидер сзади", exempt=("stack",)),
        tw.party(BEARER_XY, fp.HERO_ROD, why="жезлоносец: чужой жезл в шаге к востоку"),
    ],
}


# ------------------------------------------------------------------------ сборка

def build(t: sq.Tables, src: Path = tw.SRC, prof: dict = PROFILE) -> sg.Sg:
    scen = tw.base(src, prof)
    human, neighbor, _ = tw.players(scen)
    scen = m15.set_relation(scen, dict(NEB, relation=prof["relation"]))
    scen = tw.add_parties(scen, [tw.party(prof["foe"]["xy"], prof["foe"]["leader"])],
                          neighbor["PLAYER_ID"], t)
    scen = tw.add_rod(scen, neighbor["PLAYER_ID"], prof["rod"])
    scen = tw.add_parties(scen, prof["parties"], human["PLAYER_ID"], t)
    return tw.finish(scen, prof)


# ------------------------------------------------------------------ ожидания

def _third(rel: int, value: int) -> int:
    """Третья сторона: `rel − value/2`, деление к нулю, результат в [0, 100] (0x5D959E)."""
    return max(0, min(100, rel - int(value / 2)))


def expectation_rows(start: int, third_start: int = 0) -> list[dict]:
    """Пара Империя — Кланы и пара нейтралы — Империя после атаки и после сноса жезла.

    «вычет» — гипотеза, что атака при мире отнимает то же, что в прогоне 4 (75 − 66 = 9)."""
    g = fp.gvars()
    pc, rod = int(g["D_ATTAK_PC"]), int(g["D_ROD"])
    minus = 75 - pc
    atk = {"присваивание": (pc, pc), "вычет": (start - minus, -minus)}
    rows = []
    for hyp, (pair, value) in atk.items():
        third = _third(third_start, value)
        rows.append({"шаг": "после атаки", "гипотеза": hyp, "0-3": pair, "4-0": third,
                     "сырое": pair})
        rows.append({"шаг": "после сноса жезла", "гипотеза": hyp,
                     "0-3": max(0, min(100, pair + rod)), "4-0": _third(third, rod),
                     "сырое": pair + rod})
    return rows


# ------------------------------------------------------------------------ сторожа

def check_relation(ch: tw.Pairs, scen: sg.Sg) -> list[dict]:
    word = m15.pair_word(scen, NEB)
    cur, prev = (word >> 1) & 0x7F, (word >> 8) & 0x7F
    g = fp.gvars()
    ch.eq("RELATION Империя — Кланы, текущее", cur, PROFILE["relation"])
    ch.eq("RELATION Империя — Кланы, прошлое", prev, PROFILE["relation"])
    ch.eq("союза нет (бит 0; при союзе атака и снос запрещены, 0x5F7EBD)", word & 1, 0)
    ch.is_true("старт в полосе мира (> D_NEUTRAL)", cur > int(g["D_NEUTRAL"]), str(cur))
    neutral = sp.race_number(sc.RACE_NEUTRAL)
    for a, b, w in m15.diplomacy_pairs(scen):
        if neutral in (a, b):
            ch.eq("пара %d-%d с нейтралами — война, 0" % (a, b), w, 0)
    rows = expectation_rows(cur)
    for step in ("после атаки", "после сноса жезла"):
        got = [r for r in rows if r["шаг"] == step]
        ch.is_true("%s: гипотезы дают разные числа" % step, got[0]["0-3"] != got[1]["0-3"],
                   str([r["0-3"] for r in got]))
        ch.is_true("%s: ни одна гипотеза не упёрлась в 0 или 100" % step,
                   all(0 < r["сырое"] < 100 for r in got), str([r["сырое"] for r in got]))
    return rows


def check_foe(ch: tw.Pairs, t: sq.Tables, scen: sg.Sg) -> None:
    human, neighbor, _ = tw.players(scen)
    foes = tw.field_stacks(scen, neighbor["PLAYER_ID"])
    ch.eq("отрядов соседа на карте", len(foes), 1)
    got = tw.stack_at(scen, neighbor["PLAYER_ID"], PROFILE["foe"]["xy"])
    ch.is_true("отряд соседа в %s" % (PROFILE["foe"]["xy"],), got is not None)
    if got is None:
        return
    _, foe = got
    attackers = [s for _, s in tw.field_stacks(scen, human["PLAYER_ID"])
                 if fp.cheb(tw.xy_of(s), tw.xy_of(foe)) == 1]
    ch.eq("наших отрядов в шаге от мишени", len(attackers), 1)
    if attackers:
        ours = tw.stack_units(t, scen, attackers[0])
        tw.check_battle(ch, t, "атака на отряд соседа", ours,
                        [u for u, _, _ in tw.stack_units(t, scen, foe)], 0)


def check_rod(ch: tw.Pairs, t: sq.Tables, scen: sg.Sg) -> None:
    human, neighbor, _ = tw.players(scen)
    all_rods = tw.rods(scen)
    ch.eq("жезлов на карте", len(all_rods), 1)
    if not all_rods:
        return
    rod = all_rods[0]
    xy = (rod["POS_X"], rod["POS_Y"])
    ch.eq("владелец жезла — сосед", rod["OWNER"], neighbor["PLAYER_ID"])
    ch.eq("жезл на месте", xy, PROFILE["rod"])
    grid = sg.tile_grid(scen)
    ch.eq("под жезлом равнина", grid[xy[1]][xy[0]] >> 3, 0)
    towns = [c for r in scen.find(sg.WHAT_CAPITAL) for c in fp.capital_tiles_at(sp.city_xy(r))]
    ch.is_true("жезл не у города", min(fp.cheb(xy, c) for c in towns) >= fp.ROD_CITY_GAP)
    bearers = [s for _, s in tw.field_stacks(scen, human["PLAYER_ID"])
               if tw.leader_cat(t, scen, s) == fp.CAT_ROD and fp.cheb(tw.xy_of(s), xy) == 1]
    ch.eq("жезлоносцев в шаге от жезла", len(bearers), 1)


def verify(path: Path, t: sq.Tables, quiet: bool = False, prof: dict = PROFILE) -> None:
    scen = sg.Sg.read(path)
    ch = tw.Pairs("проверка чтением %s" % prof["file"])
    tw.check_common(ch, t, scen, prof)
    tw.check_garrisons(ch, scen, KEY)
    check_relation(ch, scen)
    check_foe(ch, t, scen)
    check_rod(ch, t, scen)
    tw.check_safety(ch, t, scen, prof)
    ch.done(quiet)


# ------------------------------------------------------------------------ печать

def print_plan(t: sq.Tables, scen: sg.Sg, prof: dict = PROFILE) -> None:
    human, neighbor, _ = tw.players(scen)
    word = m15.pair_word(scen, NEB)
    cover, who = m15.enemy_cover(t, scen, dict(NEB, turns=tw.AI_TURNS))
    print("%s -> %s: атака при мире и чужой жезл, казна %s" % (KEY, prof["name"], prof["bank"]))
    print("   RELATION 0-3 = %d (0x%04X): текущее %d, прошлое %d, союз %d"
          % (word, word, (word >> 1) & 0x7F, (word >> 8) & 0x7F, word & 1))
    print("   мишень: отряд соседа %s (%s); атакующий %s; жезл соседа %s, жезлоносец %s"
          % (FOE_XY, t.name(prof["foe"]["leader"]), ATTACKER_XY, ROD_XY, BEARER_XY))
    foe = tw.stack_at(scen, neighbor["PLAYER_ID"], FOE_XY)[1]
    ours = tw.stack_at(scen, human["PLAYER_ID"], ATTACKER_XY)[1]
    b = tw.battle(t, tw.stack_units(t, scen, ours),
                  [u for u, _, _ in tw.stack_units(t, scen, foe)], 0)
    print("   бой: раундов %s, худший входящий за раунд %s" % (b["rounds"], b["incoming"]))
    print("   угроза соседа за %d ход: %d клеток (%s)" % (tw.AI_TURNS, cover, who))
    print("   %-18s %-13s %6s %6s" % ("шаг", "гипотеза", "0-3", "4-0"))
    for r in expectation_rows((word >> 1) & 0x7F):
        print("   %-18s %-13s %6d %6d" % (r["шаг"], r["гипотеза"], r["0-3"], r["4-0"]))


# ---------------------------------------------------------------------- selftest

def _drop(scen: sg.Sg, obj_id: str) -> sg.Sg:
    return tw.rebuild_plan(replace(scen, records=tuple(r for r in scen.records
                                                       if r.obj_id != obj_id)))


def sabotage(scen: sg.Sg, what: str, t: sq.Tables) -> sg.Sg:
    human, neighbor, _ = tw.players(scen)
    if what in ("relation75", "relation50"):
        return m15.set_relation(scen, dict(NEB, relation=int(what[-2:])))
    if what == "alliance":
        pairs = [(a, b, w | 1 if w == m15.pair_word(scen, NEB) else w)
                 for a, b, w in m15.diplomacy_pairs(scen)]
        return sp.set_diplomacy(scen, pairs)
    rod = tw.rods(scen)[0]["rec"]
    foe_rec, _ = tw.stack_at(scen, neighbor["PLAYER_ID"], FOE_XY)
    bearer, _ = tw.stack_at(scen, human["PLAYER_ID"], BEARER_XY)
    if what == "norod":
        return _drop(scen, rod.obj_id)
    if what == "rodowner":
        body = rod.body.replace(neighbor["PLAYER_ID"].encode(), human["PLAYER_ID"].encode(), 1)
        return scen.with_record(rod, replace(rod, body=body))
    if what == "nobearer":
        return _drop(scen, bearer.obj_id)
    if what == "bearerfar":
        return tw.rebuild_plan(tw.patch_stack(scen, bearer, POS_Y=BEARER_XY[1] + 3))
    if what == "nofoe":
        return _drop(scen, foe_rec.obj_id)
    if what == "strongfoe":                  # Небирос: 2600 HP, 300 урона по всему строю
        return c4.set_hero_type(scen, foe_rec, "g000uu8001")
    if what == "nocapstack":
        return mp.sabotage_map(scen, what, t)
    return tw.common_sabotage(scen, what, PROFILE, t)


SABOTAGE = tw.COMMON_SABOTAGE + [
    ("nocapstack", "у чужой столицы пустой гарнизон"),
    ("relation75", "старт 75 — гипотезы атаки неразличимы"),
    ("relation50", "старт в нейтралитете, а не в мире"),
    ("alliance", "союз взведён — атака и снос запрещены"),
    ("norod", "жезла соседа нет"), ("rodowner", "жезл наш, а не соседа"),
    ("nobearer", "жезлоносца нет"), ("bearerfar", "жезлоносец отведён от жезла"),
    ("nofoe", "отряда соседа нет"), ("strongfoe", "отряд соседа не по зубам")]


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
