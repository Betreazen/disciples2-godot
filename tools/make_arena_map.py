#!/usr/bin/env python3
"""Арена Ф6 v2: карта `arena.sg` и маршрут `arena_route.json` — пять боёв, пять классов атак.

    python tools/make_arena_map.py
    python tools/make_arena_map.py --plan       # только расчёт, без записи
    python tools/make_arena_map.py --selftest   # сторожа расчёта (то же, что arena_plan.py)

Первая арена (коммит `c08cdfb`) отыграна 08.09.2026. Итог: `Summon` и `BestowWards` сняты
(15 и 4 события) и **из арены убраны насовсем**; на третьем бою отряд погиб целиком и прогон
встал. Причина названа числом: расчёт считал врага статичным, а Оккультист призвал за бой
**пять Зомби** (`g000uu0087`, 170 HP, 50 урона) — это +250 урона в раунд и +850 HP на стороне
врага, которых в таблице не было. Отряд вошёл в тот бой уже потрёпанным: отдыха между боями
маршрут не давал.

Правило, на котором держится вся конструкция, прогон **подтвердил**: у всех участников того
боя `Gattacks.REACH = 3`, и до заднего ряда никто не дотянулся — убил снежный ком призыва, а
не задний ряд.

> **Ближний бой (`Gattacks.REACH = 3`, `L_ADJACENT`) бьёт только ПЕРЕДНИЙ ряд.** Задний ряд
> открывается, лишь когда передний вымер целиком.

Состав отряда, состав боёв и весь расчёт живут в `tools/arena_plan.py` (там же `--selftest`);
здесь — карта, маршрут и проверка записанного файла. Отсюда пять правок против пяти найденных
причин, каждая числом:

1. **Ни одного призывателя** — ни носителем, ни телохранителем, ни в довеске, ни в паре ИИ.
   Проверяет `check_no_summoners` по `Gattacks.CLASS != 17` (`L_SUMMON`) обеих колонок атак
   каждого юнита ЛЮБОГО чужого отряда; печатается всегда, сборка падает на первом же
   призывателе. Это прямой ответ на то, что убило первый прогон.
2. **Отдых между боями.** После каждого боя `REST_TURNS` холостых ходов по дальней полосе
   `REST_Y`: `Gunits.REGEN = 5` у всех наших юнитов, то есть 5 % максимума за ход на карте.
   Холостой ход обязан быть настоящим шагом — `map_route.play_turn` падает, если после двух
   кликов не пришло ни одного `stack_move`, — поэтому полоса проходится клетка за клеткой, а
   не кликом по себе.
3. **Площадная атака считается по числу наших юнитов.** У Тиамат ОБЕ атаки `REACH = 1`
   (`L_ALL`): 100 урона умножаются на весь наш строй, а не идут в одного. `Numbers.dpr`
   умножает на `targets`, и бой с Тиамат стоит **последним** — если отряд после него погибнет,
   три случая `LowerDamage` уже сняты.
4. **Здоровье переносится из боя в бой.** `plan_rounds` ведёт HP отряда сквозь все пять боёв:
   потери боя вычитаются, регенерация холостых ходов прибавляется, потолок — максимум отряда.
   Прежний расчёт входил в каждый бой полным отрядом; это и скрыло гибель на третьем бою.
5. **`Cure` не сработает вхолостую.** Матриарх снимает негативные эффекты **со своих**, значит
   накладывать их обязаны мы. В отряд добавлен Master Thug (`g000uu5131`): вторая атака — яд
   (`g000aa5531`, `CLASS = 12`, `REACH = 3`, 20 урона, 80 %), инициатива 75 против 10 у
   Матриарха, то есть яд ложится **первым в раунде**, до её хода. Цель яда — телохранитель
   Yataa' Halli: в `GimmuC` иммунитета к классу 12 у него нет (там всего 7 строк, все —
   големы и гаргульи), а вот Костяной лорд иммунен к источнику 3 и потому в носители `Cure`
   не годится.

Остальное — как в первой арене и по той же причине: носитель стоит в ЗАДНЕМ ряду за
телохранителем, наш урон низкий, лечение высокое, лорд «Полководец» (пеший Pegasus Knight,
`REACH 3`), а не «Маг» (Архимаг, `REACH 1` — площадная атака достала бы носителя первой).

Проверка идеи «ИИ против ИИ» — отдельная пара отрядов в стороне от маршрута, см. `AI_PROBE`.
"""
from __future__ import annotations

import argparse
import json
import sys
from dataclasses import replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402
import make_c4_map as c4  # noqa: E402
import scenario_c4 as sc  # noqa: E402
import scenario_squads as sq  # noqa: E402
import arena_plan as ap  # noqa: E402  (состав отряда и боёв, числа и сторожа)
from arena_plan import (AI_PROBE_UNITS, BATTLES, HERO_TYPE, LEADER_CELL, LORD, REST_TURNS,
                        SQUAD, plan_rounds, print_plan)  # noqa: E402

MODDING = Path(__file__).resolve().parents[2] / "disciples-modding"
OUT = MODDING / "game-dev" / "scenarios" / "arena.sg"
ROUTE_OUT = Path(__file__).with_name("arena_route.json")

GATE = (c4.CAPITAL_XY[0] + c4.CAPITAL_SIZE, c4.CAPITAL_XY[1] + c4.CAPITAL_SIZE)   # (10,10)


# ----------------------------------------------------------------- геометрия

ARENA_Y = 5          # ряд, где стоят противники
APPROACH_Y = 8       # ряд, с которого нападаем: 3 клетки до цели — это 6 очков из 20
REST_Y = 9           # полоса отдыха: от неё до противников 4 клетки, и это же предел `sc.REACH`
ARENA_X = [13, 18, 23, 28, 33]
MIN_GAP = 2          # соседство в игре — восемь клеток вокруг, значит «не сосед» это 2

# Проверка идеи пользователя «стравить два ИИ». Оба отряда принадлежат нейтральному игроку —
# ДРУГОГО игрока-ИИ на карте нет, и добавить его нельзя без риска для основной цели (разбор в
# отчёте: экран «Выбор расы» драйвер жмёт вслепую, вторая играбельная раса может оказаться
# выбранной по умолчанию и увести прогон на чужую сторону). Поэтому проверяется то, что можно
# проверить бесплатно: **шевелится ли вообще отряд ИИ по приказу**. Обоим выдан приказ
# `L_ATTACK_STACK` (LOrder.dbf ID 4) друг на друга, стоят они вплотную и в стороне от маршрута.
# Ответ читается по журналу: `stack_move` с чужим id — приказ работает; лишний `battle_start` —
# работает и бой между ИИ пишется. Ни того ни другого — тоже результат.
AI_PROBE = [(20, 1), (21, 1)]
ORDER_ATTACK_STACK = 4              # Globals/LOrder.DBF: L_ATTACK_STACK

BANK = "G1000:R1000:Y1000:E1000:W1000:B1000"   # арена ничего не строит; казна просто не мешает


# --------------------------------------------------------------- правка тела

_STACK_TO_ORDER = sg._STACK_TAIL.split(" ORDER:i", 1)[0]
_ORDER_FIELDS = "ORDER:i ORDER_TARG:s AIORDER:i AIORDERTAR:s"


def _cursor_to_stack_tail(body: bytes) -> sg.Cursor:
    c = sg.Cursor(body)
    c.fields(sg._GROUP)
    for _ in range(c.count()):
        c.str("ITEM_ID")
    return c


def set_order(scen: sg.Sg, rec: sg.Record, order: int, target: str) -> sg.Sg:
    """Переписать ORDER/ORDER_TARG и их пару для ИИ; остальное тело — байт в байт."""
    c = _cursor_to_stack_tail(rec.body)
    c.fields(_STACK_TO_ORDER)
    start = c.pos
    c.fields(_ORDER_FIELDS)
    body = (rec.body[:start] + sg.f_int("ORDER", order) + sg.f_str("ORDER_TARG", target)
            + sg.f_int("AIORDER", order) + sg.f_str("AIORDERTAR", target) + rec.body[c.pos:])
    return scen.with_record(rec, replace(rec, body=body))


def set_leader_cell(scen: sg.Sg, rec: sg.Record, cell: int) -> sg.Sg:
    """Переставить единственного юнита отряда (лидера) в клетку строя `cell`.

    Работает только на отряде из одного юнита — то есть ДО добавления солдат: `place` потом
    сохраняет уже занятые клетки, и герой останется там, куда его поставили здесь.
    """
    st = sg.parse_stack(rec.body)
    filled = [k for k in range(6) if st[f"UNIT_{k}"] != sg.NULL_ID]
    if filled != [0]:
        raise SystemExit("%s: в отряде %d юнитов, лидера переставляют до добавления солдат"
                         % (rec.obj_id, len(filled)))
    cells = [-1] * 6
    cells[cell] = 0
    c = sg.Cursor(rec.body)
    c.fields(sg._GROUP)
    prefix = (sg.f_str("GROUP_ID", st["GROUP_ID"])
              + b"".join(sg.f_str(f"UNIT_{k}", st[f"UNIT_{k}"]) for k in range(6))
              + b"".join(sg.f_int(f"POS_{k}", v) for k, v in enumerate(cells)))
    return scen.with_record(rec, replace(rec, body=prefix + rec.body[c.pos:]))



# ---------------------------------------------------------------------- сборка

def build(template: Path, name: str = "arena") -> tuple[sg.Sg, dict, list[dict]]:
    t = sq.Tables()
    rows = plan_rounds(t)
    scen = sg.Sg.read(template)
    size = len(sg.tile_grid(scen))
    scen, changed = c4.set_terrain(scen, c4.wanted_tiles(size))
    scen = c4.move_capital(scen, *c4.CAPITAL_XY)

    hero = next(r for r in scen.find(sg.WHAT_STACK)
                if sg.parse_stack(r.body)["INSIDE"] != sg.NULL_ID)
    scen = c4.move_stack(scen, hero, *c4.CAPITAL_XY)
    scen = c4.set_hero_type(scen, hero, HERO_TYPE)
    keep = len(BATTLES) + len(AI_PROBE)
    scen, dropped = c4.drop_neutral_stacks(scen, keep=keep)

    hero = next(r for r in scen.find(sg.WHAT_STACK) if r.obj_id == hero.obj_id)
    scen = set_leader_cell(scen, hero, LEADER_CELL)
    hero = next(r for r in scen.find(sg.WHAT_STACK) if r.obj_id == hero.obj_id)
    scen, _ = sq.add_soldiers(scen, hero, SQUAD, scen.next_ids("UN", len(SQUAD)), t)

    hero_owner = sg.parse_stack(hero.body)["OWNER"]
    neutral = [r for r in scen.find(sg.WHAT_STACK)
               if sg.parse_stack(r.body)["OWNER"] != hero_owner]
    if len(neutral) != keep:
        raise SystemExit("нейтральных отрядов %d, ожидалось %d" % (len(neutral), keep))

    for rec, (cls, carrier, guard, extra, _why), x in zip(neutral, BATTLES, ARENA_X):
        scen = c4.move_stack(scen, rec, x, ARENA_Y)
        rec = next(r for r in scen.find(sg.WHAT_STACK) if r.obj_id == rec.obj_id)
        scen = c4.set_hero_type(scen, rec, guard)
        rec = next(r for r in scen.find(sg.WHAT_STACK) if r.obj_id == rec.obj_id)
        # У боя DrainOverflow носитель и телохранитель — один и тот же юнит: он уже лидер.
        soldiers = ([] if carrier == guard else [carrier]) + list(extra)
        if soldiers:
            scen, _ = sq.add_soldiers(scen, rec, soldiers, scen.next_ids("UN", len(soldiers)), t)

    probes = neutral[len(BATTLES):]
    for rec, xy in zip(probes, AI_PROBE):
        scen = c4.move_stack(scen, rec, *xy)
        rec = next(r for r in scen.find(sg.WHAT_STACK) if r.obj_id == rec.obj_id)
        scen, _ = sq.add_soldiers(scen, rec, AI_PROBE_UNITS,
                                  scen.next_ids("UN", len(AI_PROBE_UNITS)), t)
    for rec, other in zip(probes, reversed(probes)):
        rec = next(r for r in scen.find(sg.WHAT_STACK) if r.obj_id == rec.obj_id)
        scen = set_order(scen, rec, ORDER_ATTACK_STACK, other.obj_id)

    player = next(p for p in (sg.fields(r.body, sg.LAYOUT_PLAYER)
                              for r in scen.find(sg.WHAT_PLAYER))
                  if p["RACE_ID"] != sc.RACE_NEUTRAL)
    scen = set_bank(scen, player["PLAYER_ID"], BANK)
    scen = c4.rebuild_plan(scen)
    scen = sg.set_scenario_name(scen, name)
    return scen, {"клеток перерисовано": changed, "отрядов удалено": len(dropped),
                  "боёв": len(BATTLES), "пара ИИ": len(AI_PROBE),
                  "записей": len(scen.records)}, rows


_PLAYER_TO_BANK = ("PLAYER_ID:s NAME_TXT:s DESC_TXT:s LORD_ID:s RACE_ID:s FOG_ID:s "
                   "KNOWN_ID:s BUILDS_ID:s FACE:i QTY_BREAKS:i")


def set_bank(scen: sg.Sg, player_id: str, bank: str) -> sg.Sg:
    for rec in scen.find(sg.WHAT_PLAYER):
        if sg.fields(rec.body, "PLAYER_ID:s")["PLAYER_ID"] != player_id:
            continue
        c = sg.Cursor(rec.body)
        c.fields(_PLAYER_TO_BANK)
        start = c.pos
        c.str("BANK")
        return scen.with_record(rec, replace(rec, body=rec.body[:start]
                                             + sg.f_str("BANK", bank) + rec.body[c.pos:]))
    raise SystemExit("запись игрока %s не найдена" % player_id)


# ------------------------------------------------------------------- маршрут

def turn_targets() -> list[tuple[tuple[int, int], str, str | None]]:
    """Цели ходов подряд: (клетка, пояснение, класс боя или None у холостого хода).

    Между боями стоят `REST_TURNS` холостых ходов по полосе `REST_Y`. Холостой ход обязан быть
    НАСТОЯЩИМ шагом: `map_route.play_turn` падает, если после двух кликов не пришло ни одного
    `stack_move`, — поэтому цели разные, а не «кликнуть по себе». Заодно каждый шаг короче
    `sc.REACH` = 4 клеток по Чебышёву: цель дальше обзора движок не обсчитывает.
    """
    out: list[tuple[tuple[int, int], str, str | None]] = [(GATE, "выход из столицы", None)]
    for k, ((cls, *_rest), x) in enumerate(zip(BATTLES, ARENA_X)):
        if k:
            prev = ARENA_X[k - 1]
            mid = (prev + x) // 2
            for j, cell in enumerate(((prev, REST_Y), (mid, REST_Y), (x, REST_Y))[:REST_TURNS]):
                out.append((cell, "отдых %d из %d после боя %d (регенерация на карте)"
                            % (j + 1, REST_TURNS, k), None))
        out.append(((x, APPROACH_Y), "подход к бою %s" % cls, cls))
    return out


def walk(scen: sc.Scenario, targets: list[tuple[tuple[int, int], str, str | None]],
         start: tuple[int, int]) -> list[list[tuple[int, int]]]:
    """Отрезки пути по ходам. После боя отряд стоит НА клетке противника, а не на цели хода —
    следующий ход движок проложит уже оттуда, и считать надо так же."""
    legs, pos = [], start
    for k, (target, _note, cls) in enumerate(targets):
        if k == 0:
            # Выход из столицы. Отряд стоит ВНУТРИ пятна 5x5, и путь до ворот движок строит сам
            # — ни Дейкстра по клеткам, ни предел обзора здесь не применимы: живой прогон
            # 08.09.2026 прошёл этот ход первым же и дошёл до третьего боя.
            legs.append([pos, target])
            pos = target
            continue
        leg = sc.dijkstra(scen, pos, target)
        if len(leg) < 2:
            raise SystemExit("ход %d: цель %s совпала с началом — шага не будет, а без "
                             "stack_move play_turn падает" % (k + 1, target))
        if sc.chebyshev(pos, target) > sc.REACH:
            raise SystemExit("ход %d: цель %s дальше %d клеток от %s — туман войны"
                             % (k + 1, target, sc.REACH, pos))
        legs.append(leg)
        pos = (target[0], ARENA_Y) if cls else target      # после боя отряд на клетке врага
    return legs


def check_gaps(legs: list[list[tuple[int, int]]],
               targets: list[tuple[tuple[int, int], str, str | None]]) -> None:
    """Ни одна клетка маршрута не смеет стать соседом ЕЩЁ НЕ отвоёванного противника: шаг
    вплотную начал бы бой вне очереди, до действий хода."""
    enemies = [(x, ARENA_Y) for x in ARENA_X]
    fought = 0
    for k, (leg, (_target, note, cls)) in enumerate(zip(legs, targets)):
        live = enemies[fought:] + AI_PROBE
        pairs = [(sc.chebyshev(c, e), c, e) for c in leg for e in live]
        d, cell, foe = min(pairs) if pairs else (99, (0, 0), (0, 0))
        print("  ход %2d %-46s клеток %2d, зазор до живых %d" % (k + 1, note, len(leg) - 1, d))
        if d < MIN_GAP:
            raise SystemExit("ход %d: клетка %s вплотную к %s — бой начнётся вне очереди"
                             % (k + 1, cell, foe))
        if cls:
            fought += 1


def build_route(scen_path: Path, rows: list[dict]) -> dict:
    """Ходы: выход из столицы, подход с боем на каждый класс и `REST_TURNS` холостых между.

    Ход НЕ кончается вплотную к противнику: движение хода идёт до действий хода
    (`corpus_f6_run.play_map`), и бой, начатый шагом, пришёл бы вне очереди. На противника
    шагает действие `attack` — три клетки, шесть очков из двадцати.
    """
    scen = sc.Scenario(scen_path)
    stack = scen.own_stacks()[0]
    budget = int(stack["MOVE"]) or sc.DEFAULT_MOVE
    targets = turn_targets()
    legs = walk(scen, targets, (int(stack["POS_X"]), int(stack["POS_Y"])))
    check_gaps(legs, targets)
    check_budget(scen, legs, targets, budget)

    why = {cls: text for cls, _c, _g, _e, text in BATTLES}
    who = {cls: (carrier, guard) for cls, carrier, guard, _e, _w in BATTLES}
    turns = []
    for k, ((target, note, cls), leg) in enumerate(zip(targets, legs)):
        actions = []
        if k == 0:
            actions.append({"do": "verify_squad", "when": "before",
                            "text": "состав отряда после старта сцены: шесть юнитов, из них два "
                                    "гвардейца и Master Thug в переднем ряду, герой и два "
                                    "архангела в заднем"})
        if cls:
            carrier, guard = who[cls]
            actions.append({"do": "attack", "target": [target[0], ARENA_Y], "kind": cls,
                            "note": "%s: носитель %s, телохранитель %s. %s"
                                    % (cls, carrier, guard, why[cls])})
        turns.append({"turn": k + 1, "target": list(target), "note": note, "actions": actions})

    path = [legs[0][0]]
    for leg in legs:
        path += leg[1:]
    expect_class = {str(CLASS_ID[cls]): 2 for cls, *_ in BATTLES}
    return {
        "scenario": scen.name,
        "stack": stack["STACK_ID"],
        "move_budget": budget,
        "lord": LORD,
        "waypoints": [list(t) for t, _n, cls in targets if cls],
        "path": [list(p) for p in path],
        "turns": turns,
        "checkpoints": [],
        "require_actions": ["attack", "verify_squad"],
        "expect": {"battle_start": len(BATTLES), "battle_end": len(BATTLES),
                   "attack_new": 1, "attack_get": 1, "battle_turn": 1, "round": 1},
        "expect_class": expect_class,
        "arena": {"battles": [{"класс": c, "носитель": u, "телохранитель": g, "клетка": [x, ARENA_Y]}
                              for (c, u, g, _e, _w), x in zip(BATTLES, ARENA_X)],
                  "squad": SQUAD, "leader_cell": LEADER_CELL, "rest_turns": REST_TURNS,
                  "rest_row": REST_Y,
                  "ai_probe": [list(p) for p in AI_PROBE],
                  "plan": rows},
    }


def check_budget(scen: sc.Scenario, legs: list[list[tuple[int, int]]],
                 targets: list[tuple[tuple[int, int], str, str | None]], budget: int) -> None:
    """Ход обязан помещаться в очки движения, а на ходу с боем — ещё и шаг на противника."""
    for k, (leg, (target, _note, cls)) in enumerate(zip(legs, targets)):
        if k == 0:
            continue        # выход из столицы: отрезок синтетический, считать его нечестно
        cost = sum(sc.STEP_COST[scen.ground(*c)] for c in leg[1:])
        if cls:
            strike = sc.dijkstra(scen, target, (target[0], ARENA_Y))
            cost += sum(sc.STEP_COST[scen.ground(*c)] for c in strike[1:])
        if cost > budget:
            raise SystemExit("ход %d: %d очков движения из %d — отряд не дойдёт%s"
                             % (k + 1, cost, budget, " и не ударит" if cls else ""))


# Gattacks.CLASS / Globals/LattC.dbf — те же номера, что пишет зонд в `disp_class`.
CLASS_ID = {"Fear": 7, "LowerDamage": 10, "DrainOverflow": 15, "Cure": 16,
            "TransformOther": 22}


# ------------------------------------------------------------------- проверка

def verify(path: Path, t: sq.Tables) -> None:
    """Прочитать записанный файл обратно и проверить числами всё, что генератор обещал."""
    scen = sg.Sg.read(path)
    types = {r.obj_id.upper(): sg.fields(r.body, sg.LAYOUT_UNIT)["TYPE"].lower()
             for r in scen.find(sg.WHAT_UNIT)}
    players = [sg.fields(r.body, sg.LAYOUT_PLAYER) for r in scen.find(sg.WHAT_PLAYER)]
    playable = [p for p in players if p["RACE_ID"] != sc.RACE_NEUTRAL]
    assert len(playable) == 1, "играбельных игроков %d — экран «Выбор расы» станет неоднозначным" % len(playable)

    stacks = {r.obj_id: sg.parse_stack(r.body) for r in scen.find(sg.WHAT_STACK)}
    own = [s for s in stacks.values() if s["OWNER"] == playable[0]["PLAYER_ID"]]
    assert len(own) == 1, "своих отрядов %d" % len(own)
    st = own[0]
    members = {k: types[st[f"UNIT_{k}"].upper()] for k in range(6) if st[f"UNIT_{k}"] != sg.NULL_ID}
    got = [types[st[f"UNIT_{k}"].upper()] for k in range(1, 6) if st[f"UNIT_{k}"] != sg.NULL_ID]
    assert got == SQUAD, "состав отряда %s, ожидался %s" % (got, SQUAD)
    cells = {k: st[f"POS_{k}"] for k in range(6)}
    assert cells[LEADER_CELL] == 0, "герой не в клетке %d: строй %s" % (LEADER_CELL, cells)
    front = [members[cells[c]] for c in (0, 2, 4) if cells[c] >= 0]
    assert all(t.is_melee(u) for u in front), "в переднем ряду не только ближники: %s" % front
    assert HERO_TYPE not in front, "герой оказался в переднем ряду"

    foes = [s for s in stacks.values() if s["OWNER"] != playable[0]["PLAYER_ID"]]
    assert len(foes) == len(BATTLES) + len(AI_PROBE), "чужих отрядов %d" % len(foes)
    by_pos = {(int(s["POS_X"]), int(s["POS_Y"])): s for s in foes}
    for (cls, carrier, guard, extra, _why), x in zip(BATTLES, ARENA_X):
        s = by_pos[(x, ARENA_Y)]
        leader = types[s["LEADER_ID"].upper()]
        assert leader == guard, "%s: лидер %s, ожидался %s" % (cls, leader, guard)
        mem = {k: types[s[f"UNIT_{k}"].upper()] for k in range(6) if s[f"UNIT_{k}"] != sg.NULL_ID}
        want = ([] if carrier == guard else [carrier]) + list(extra)
        soldiers = [types[s[f"UNIT_{k}"].upper()] for k in range(1, 6)
                    if s[f"UNIT_{k}"] != sg.NULL_ID]
        assert soldiers == want, "%s: солдаты %s, ожидалось %s" % (cls, soldiers, want)
        # Носитель обязан стоять в ЗАДНЕМ ряду — иначе весь замысел арены не работает.
        if carrier != guard and not t.is_big(carrier) and not t.is_melee(carrier):
            back = [mem[s[f"POS_{c}"]] for c in (1, 3, 5) if s[f"POS_{c}"] >= 0]
            assert carrier in back, "%s: носитель %s не в заднем ряду (%s)" % (cls, carrier, back)

    orders = read_orders(scen, [by_pos[xy]["STACK_ID"] for xy in AI_PROBE])
    for sid, (order, target) in orders.items():
        assert order == ORDER_ATTACK_STACK, "%s: приказ %s, ожидался %d" % (sid, order, ORDER_ATTACK_STACK)
        assert target.upper() in {s.upper() for s in orders} and target.upper() != sid.upper(), \
            "%s: цель приказа %s" % (sid, target)

    size, plan = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    ids = {r.obj_id for r in scen.records}
    dangling = sorted({e for _, _, e in plan} - ids)
    assert not dangling, "в плане ссылки на удалённые записи: %s" % dangling
    for r in scen.find(sg.WHAT_STACK):
        s = sg.parse_stack(r.body)
        for k in range(6):
            u = s[f"UNIT_{k}"]
            assert u == sg.NULL_ID or u in ids, "%s: юнит %s удалён, ссылка осталась" % (r.obj_id, u)
    print("проверка чтения: игрок, строй отряда, пять боёв, задний ряд носителей, "
          "приказы пары ИИ и план сошлись")


def read_orders(scen: sg.Sg, stack_ids: list[str]) -> dict[str, tuple[int, str]]:
    out = {}
    want = {s.upper() for s in stack_ids}
    for r in scen.find(sg.WHAT_STACK):
        s = sg.parse_stack(r.body)
        if s["STACK_ID"].upper() not in want:
            continue
        out[s["STACK_ID"]] = (int(s["ORDER"]), str(s["ORDER_TARG"]))
    return out


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--template", type=Path, default=c4.TEMPLATE)
    p.add_argument("--out", type=Path, default=OUT)
    p.add_argument("--route", type=Path, default=ROUTE_OUT)
    p.add_argument("--name", default="arena")
    p.add_argument("--plan", action="store_true", help="только расчёт, ничего не записывать")
    p.add_argument("--selftest", action="store_true", help="проверить сторожей подсадной ошибкой")
    a = p.parse_args(argv)
    if a.selftest:
        return ap.selftest()
    t = sq.Tables()
    if a.plan:
        print_plan(t)
        return 0
    if not a.template.exists():
        raise SystemExit("нет шаблона %s" % a.template)
    print_plan(t)
    print()
    scen, stats, rows = build(a.template, a.name)
    a.out.parent.mkdir(parents=True, exist_ok=True)
    scen.write(a.out)
    print("%s: %s" % (a.out, ", ".join("%s %s" % kv for kv in stats.items())))
    verify(a.out, t)
    route = build_route(a.out, rows)
    a.route.write_text(json.dumps(route, ensure_ascii=False, indent=2), encoding="utf-8")
    print("маршрут %s: ходов %d, боёв %d"
          % (a.route, len(route["turns"]), len(BATTLES)))
    for turn in route["turns"]:
        acts = ", ".join(x["do"] + (":" + x["kind"] if x.get("kind") else "")
                         for x in turn.get("actions", []))
        print("  ход %d -> %s%s" % (turn["turn"], tuple(turn["target"]),
                                    ("  " + acts) if acts else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
