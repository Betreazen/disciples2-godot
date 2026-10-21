#!/usr/bin/env python3
"""Арена предметов Ф6: карта `items.sg` и маршрут `items_route.json` — три боя РУКАМИ.

    python tools/make_items_map.py
    python tools/make_items_map.py --plan       # только расчёт, без записи
    python tools/make_items_map.py --selftest   # сторожа расчёта (то же, что items_plan.py)

Состав, предметы и вся арифметика — в `tools/items_plan.py`; здесь байты карты, маршрут и
проверка записанного файла чтением обратно.

**Чем эта арена отличается от арены v2 (`make_arena_map.py`).** Там бои доигрывал автобой
(`A`), и от пользователя не требовалось ничего. Здесь автобой не годится вовсе: ИИ зелья не
пьёт, а у применения предмета хоткея нет. Поэтому бои играет человек, а маршрут несёт новое
действие `manual_battle` (`tools/corpus_f6_run.py`): драйвер печатает инструкцию, шагает на
врага и ждёт `battle_end` без потолка в четыре минуты — человек играет медленно.

**Предметы кладутся СЦЕНАРИЕМ.** Дописывать вещи в сохранение бесполезно по той же причине,
по которой бесполезно дописывать туда юнитов (`corpus_f6_run.do_verify_squad`: правленое
сохранение теряет дописанное, сценарий — сохраняет). Инвентарь лидера — список `ITEM_ID`
внутри `MidStack` плюс запись `MidItem` на каждую вещь; у талисманов сверх того есть заряды
в `MidTalismanCharges` (проверено на ванильной `'Greenskins'.sg`: две записи по 5 зарядов).

**Боевые слоты (`BATTLE1`/`BATTLE2`) остаются пустыми** — как во всех ванильных картах без
единого исключения (проверено разбором 34 карт `Exports-archive`). Раскладывает их
пользователь перед каждым боем; что при этом происходит с записью отряда, арена и выяснит:
шаг сверки печатает и слоты, и инвентарь, а падает только если вещи нет НИГДЕ.
"""
from __future__ import annotations

import argparse
import json
import struct
import sys
from collections import Counter
from dataclasses import replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402
import make_c4_map as c4  # noqa: E402
import scenario_c4 as sc  # noqa: E402
import scenario_squads as sq  # noqa: E402
import items_plan as ip  # noqa: E402
from make_arena_map import set_bank, set_leader_cell  # noqa: E402

MODDING = Path(__file__).resolve().parents[2] / "disciples-modding"
OUT = MODDING / "game-dev" / "scenarios" / "items.sg"
ROUTE_OUT = Path(__file__).with_name("items_route.json")

WHAT_ITEM = ".?AVCMidItem@@"
WHAT_TALISMAN = ".?AVCMidTalismanCharges@@"
SLOTS = ("BANNER", "TOME", "BATTLE1", "BATTLE2", "ARTIFACT1", "ARTIFACT2", "BOOTS")

GATE = (c4.CAPITAL_XY[0] + c4.CAPITAL_SIZE, c4.CAPITAL_XY[1] + c4.CAPITAL_SIZE)   # (10,10)
ARENA_Y = 5          # ряд, где стоят противники
APPROACH_Y = 8       # ряд, с которого нападаем: 3 клетки до цели — 6 очков из 20
REST_Y = 9           # полоса отдыха
ARENA_X = [13, 17, 21]
MIN_GAP = 2          # соседство в игре — восемь клеток вокруг, значит «не сосед» это 2
BANK = "G1000:R1000:Y1000:E1000:W1000:B1000"

EQUIP_SECONDS = 120          # сколько ждём, пока пользователь разложит два предмета по слотам
REVIVE_SECONDS = 120
BATTLE_SECONDS = 600         # потолок ожидания `battle_end` в бою, который играет человек


# ------------------------------------------------------- предметы в сохранении

def add_items(scen: sg.Sg, stack: sg.Record, types: list[str],
              new_ids: list[str]) -> tuple[sg.Sg, sg.Record]:
    """Дописать вещи в инвентарь отряда: список `ITEM_ID` в теле плюс запись `MidItem` на каждую.

    Список лежит между блоком строя (`_GROUP`) и хвостом отряда: десять байт копии id, `u32`
    длины и столько же полей `ITEM_ID`. Всё вокруг — байт в байт.
    """
    if len(types) != len(new_ids):
        raise SystemExit("предметов %d, свободных id %d" % (len(types), len(new_ids)))
    c = sg.Cursor(stack.body)
    c.fields(sg._GROUP)
    head = c.pos
    n = c.count()
    for _ in range(n):
        c.str("ITEM_ID")
    tail = c.pos
    body = (stack.body[:head + 10] + struct.pack("<I", n + len(new_ids))
            + stack.body[head + 14:tail]
            + b"".join(sg.f_str("ITEM_ID", i) for i in new_ids)
            + stack.body[tail:])
    recs = [sg.Record(WHAT_ITEM, iid, sg.f_str("ITEM_ID", iid) + sg.f_str("ITEM_TYPE", t))
            for iid, t in zip(new_ids, types)]
    new_stack = replace(stack, body=body)
    return scen.with_record(stack, new_stack).appended(*recs), new_stack


def parse_charges(body: bytes) -> list[tuple[str, int]]:
    c = sg.Cursor(body)
    return [(c.str("ID_TALIS"), c.int("CHARGES")) for _ in range(c.count())]


def set_charges(scen: sg.Sg, pairs: list[tuple[str, int]]) -> sg.Sg:
    """Заряды талисманов: `MidTalismanCharges` — счётчик и пары (id вещи, заряды)."""
    rec = scen.one(WHAT_TALISMAN)
    body = sg.f_count(rec.obj_id, len(pairs)) + b"".join(
        sg.f_str("ID_TALIS", i) + sg.f_int("CHARGES", n) for i, n in pairs)
    return scen.with_record(rec, replace(rec, body=body))


def item_types(scen: sg.Sg) -> dict[str, str]:
    return {r.obj_id: sg.fields(r.body, "ITEM_ID:s ITEM_TYPE:s")["ITEM_TYPE"]
            for r in scen.find(WHAT_ITEM)}


# ---------------------------------------------------------------------- сборка

def _same(scen: sg.Sg, rec: sg.Record) -> sg.Record:
    return next(r for r in scen.find(sg.WHAT_STACK) if r.obj_id == rec.obj_id)


def build(template: Path, name: str = "items") -> tuple[sg.Sg, dict]:
    t = sq.Tables()
    scen = sg.Sg.read(template)
    size = len(sg.tile_grid(scen))
    scen, changed = c4.set_terrain(scen, c4.wanted_tiles(size))
    scen = c4.move_capital(scen, *c4.CAPITAL_XY)

    hero = next(r for r in scen.find(sg.WHAT_STACK)
                if sg.parse_stack(r.body)["INSIDE"] != sg.NULL_ID)
    scen = c4.move_stack(scen, hero, *c4.CAPITAL_XY)
    scen = c4.set_hero_type(scen, hero, ip.HERO_TYPE)
    scen, dropped = c4.drop_neutral_stacks(scen, keep=len(ip.BATTLES))

    hero = _same(scen, hero)
    scen = set_leader_cell(scen, hero, ip.LEADER_CELL)
    hero = _same(scen, hero)
    scen, _ = sq.add_soldiers(scen, hero, ip.SQUAD, scen.next_ids("UN", len(ip.SQUAD)), t)
    hero = _same(scen, hero)
    item_ids = scen.next_ids("IM", len(ip.INVENTORY))
    scen, _ = add_items(scen, hero, ip.INVENTORY, item_ids)
    scen = set_charges(scen, [(i, ip.TALISMAN_CHARGES)
                              for i, kind in zip(item_ids, ip.INVENTORY)
                              if kind == ip.ITEM_TALISMAN])

    hero_owner = sg.parse_stack(hero.body)["OWNER"]
    foes = [r for r in scen.find(sg.WHAT_STACK)
            if sg.parse_stack(r.body)["OWNER"] != hero_owner]
    if len(foes) != len(ip.BATTLES):
        raise SystemExit("чужих отрядов %d, ожидалось %d" % (len(foes), len(ip.BATTLES)))
    for rec, b, x in zip(foes, ip.BATTLES, ARENA_X):
        scen = c4.move_stack(scen, rec, x, ARENA_Y)
        rec = _same(scen, rec)
        scen = c4.set_hero_type(scen, rec, b["leader"])
        rec = _same(scen, rec)
        if b["soldiers"]:
            scen, _ = sq.add_soldiers(scen, rec, list(b["soldiers"]),
                                      scen.next_ids("UN", len(b["soldiers"])), t)

    player = next(p for p in (sg.fields(r.body, sg.LAYOUT_PLAYER)
                              for r in scen.find(sg.WHAT_PLAYER))
                  if p["RACE_ID"] != sc.RACE_NEUTRAL)
    scen = set_bank(scen, player["PLAYER_ID"], BANK)
    scen = c4.rebuild_plan(scen)
    scen = sg.set_scenario_name(scen, name)
    return scen, {"клеток перерисовано": changed, "отрядов удалено": len(dropped),
                  "боёв": len(ip.BATTLES), "предметов": len(ip.INVENTORY),
                  "записей": len(scen.records)}


# ------------------------------------------------------------------- маршрут

def turn_targets() -> list[tuple[tuple[int, int], str, dict | None]]:
    """Цели ходов подряд: (клетка, пояснение, бой или None у холостого хода)."""
    out: list[tuple[tuple[int, int], str, dict | None]] = [(GATE, "выход из столицы", None)]
    for k, (b, x) in enumerate(zip(ip.BATTLES, ARENA_X)):
        if k:
            prev_b, prev_x = ip.BATTLES[k - 1], ARENA_X[k - 1]
            rest = int(prev_b["rest_after"])
            cells = ((prev_x, REST_Y), ((prev_x + x) // 2, REST_Y), (x, REST_Y))[:rest]
            for j, cell in enumerate(cells):
                out.append((cell, "отдых %d из %d после боя %s (регенерация на карте)"
                            % (j + 1, rest, prev_b["name"]), None))
        out.append(((x, APPROACH_Y), "подход к бою %s" % b["name"], b))
    return out


def walk(scen: sc.Scenario, targets: list[tuple[tuple[int, int], str, dict | None]],
         start: tuple[int, int]) -> list[list[tuple[int, int]]]:
    """Отрезки пути по ходам. После боя отряд стоит НА клетке противника, а не на цели хода."""
    legs, pos = [], start
    for k, (target, _note, battle) in enumerate(targets):
        if k == 0:
            legs.append([pos, target])          # выход из столицы: путь движок строит сам
            pos = target
            continue
        if sc.chebyshev(pos, target) > sc.REACH:
            raise SystemExit("ход %d: цель %s дальше %d клеток от %s — туман войны"
                             % (k + 1, target, sc.REACH, pos))
        leg = sc.dijkstra(scen, pos, target)
        if len(leg) < 2:
            raise SystemExit("ход %d: цель %s совпала с началом — без stack_move play_turn падает"
                             % (k + 1, target))
        legs.append(leg)
        pos = (target[0], ARENA_Y) if battle else target
    return legs


def check_route(scen: sc.Scenario, legs: list[list[tuple[int, int]]],
                targets: list[tuple[tuple[int, int], str, dict | None]], budget: int) -> None:
    """Три условия на каждый ход, все три печатаются числом: зазор, очки движения, шаг на врага.

    Зазор: ни одна клетка маршрута не смеет стать соседом ЕЩЁ НЕ отвоёванного противника —
    шаг вплотную начал бы бой вне очереди, до действий хода.
    """
    enemies = [(x, ARENA_Y) for x in ARENA_X]
    fought = 0
    for k, (leg, (target, note, battle)) in enumerate(zip(legs, targets)):
        live = enemies[fought:]
        pairs = [(sc.chebyshev(c, e), c, e) for c in leg for e in live]
        gap, cell, foe = min(pairs) if pairs else (99, (0, 0), (0, 0))
        cost = 0 if k == 0 else sum(sc.STEP_COST[scen.ground(*c)] for c in leg[1:])
        if battle:
            strike = sc.dijkstra(scen, target, (target[0], ARENA_Y))
            cost += sum(sc.STEP_COST[scen.ground(*c)] for c in strike[1:])
        print("  ход %2d %-52s клеток %2d, зазор %d, очков %2d из %d"
              % (k + 1, note, len(leg) - 1, gap, cost, budget))
        if gap < MIN_GAP:
            raise SystemExit("ход %d: клетка %s вплотную к %s — бой начнётся вне очереди"
                             % (k + 1, cell, foe))
        if cost > budget:
            raise SystemExit("ход %d: %d очков движения из %d — отряд не дойдёт%s"
                             % (k + 1, cost, budget, " и не ударит" if battle else ""))
        if battle:
            fought += 1


def equip_text(b: dict, t: sq.Tables, items: dict) -> str:
    names = " и ".join(t.text.get(items[i]["NAME_TXT"].lower(), i) for i in b["slots"])
    return ("бой %s: положите в ДВА боевых слота лидера %s (%s). Панель отряда на карте — "
            "щелчок по портрету лидера; в бою она же по клавише P (docs/game-hotkeys.md, "
            "DLG_BATTLE_A BTN_PAPERDOLL). Закройте окно, когда разложите."
            % (b["name"], names, ", ".join(b["slots"])))


def battle_text(b: dict, t: sq.Tables, items: dict) -> str:
    names = {i: t.text.get(items[i]["NAME_TXT"].lower(), i) for i in b["slots"]}
    steps = " ".join("%d) %s" % (n + 1, s) for n, s in enumerate(b["steps"]))
    return ("БОЙ %s — играете ВЫ, автобой (A) НЕ нажимать: ИИ зелья не пьёт. Предметы: %s. %s "
            "Драйвер ждёт конца боя до %d с и сам закроет итоги."
            % (b["name"], ", ".join(names.values()), steps, BATTLE_SECONDS))


def revive_action(t: sq.Tables) -> dict:
    return {"do": "manual", "when": "after", "seconds": REVIVE_SECONDS, "keys": [],
            "text": ("ВОСКРЕШЕНИЕ: если в отряде есть павший юнит (вероятнее всего Thug, 65 HP), "
                     "примените на него Life Potion (G000IG0001) из инвентаря лидера — панель "
                     "отряда на карте, щелчок по портрету лидера. Если все живы — ничего не "
                     "делайте, шаг только запишет состояние отряда."),
            "verify": {"kind": "inventory", "id": "?"}}


def build_route(scen_path: Path) -> dict:
    t = sq.Tables()
    items = {r["ITEM_ID"]: r for r in sq.read(sq.GLOBALS / "GItem.DBF")["rows"]}
    scen = sc.Scenario(scen_path)
    stack = scen.own_stacks()[0]
    budget = int(stack["MOVE"]) or sc.DEFAULT_MOVE
    targets = turn_targets()
    legs = walk(scen, targets, (int(stack["POS_X"]), int(stack["POS_Y"])))
    check_route(scen, legs, targets, budget)

    turns, rest_seen = [], 0
    for k, ((target, note, b), leg) in enumerate(zip(targets, legs)):
        actions: list[dict] = []
        if k == 0:
            actions.append({"do": "verify_squad", "when": "before",
                            "text": "состав отряда после старта сцены: лидер и три солдата"})
        elif b is None:
            rest_seen += 1
            if rest_seen == 1:          # первый холостой ход после боя heal — шаг воскрешения
                actions.append(revive_action(t))
        if b is not None:
            actions.append({"do": "manual", "when": "after", "seconds": EQUIP_SECONDS,
                            "keys": [], "text": equip_text(b, t, items),
                            "verify": {"kind": "battle_slots", "id": ",".join(b["slots"])}})
            battle = {"do": "manual_battle", "when": "after", "kind": b["name"],
                      "target": [target[0], ARENA_Y], "limit": BATTLE_SECONDS,
                      "text": battle_text(b, t, items)}
            if b["slots"] == [ip.ITEM_ORB, ip.ITEM_TALISMAN]:
                battle["verify"] = {"kind": "inventory",
                                    "id": "-%s,%s*2" % (ip.ITEM_ORB, ip.ITEM_TALISMAN)}
            actions.append(battle)
        turns.append({"turn": k + 1, "target": list(target), "note": note, "actions": actions})

    path = [legs[0][0]]
    for leg in legs:
        path += leg[1:]
    return {
        "scenario": scen.name, "stack": stack["STACK_ID"], "move_budget": budget, "lord": ip.LORD,
        "waypoints": [[x, ARENA_Y] for x in ARENA_X],
        "path": [list(p) for p in path],
        "turns": turns,
        "checkpoints": [],
        "require_actions": ["manual_battle", "manual", "verify_squad"],
        "expect": {"battle_start": len(ip.BATTLES), "battle_end": len(ip.BATTLES),
                   "attack_new": 1, "attack_get": 1, "battle_turn": 1, "round": 1,
                   "dmg_buffs": 1, "power": 1},
        # Классы 23 (Blister) и 13 (Frostbite) на карте не умеет ни один юнит — их появление
        # и есть прямое доказательство, что предмет-атака применён.
        "expect_class": {"1": 1, "23": 1, "13": 1},
        # Следы применения предмета (событий «применён предмет» в схеме v4 нет вовсе —
        # разбор в `tools/corpus_items.py`). Лечений ждём два: зелье и мазь.
        "expect_items": {"boost_damage": 1, "boost_power": 1, "heal": 2, "item_attack": 2},
        "items": {"inventory": ip.INVENTORY, "squad": ip.SQUAD, "leader_cell": ip.LEADER_CELL,
                  "battles": [{"бой": b["name"], "слоты": b["slots"],
                               "клетка": [x, ARENA_Y], "зачем": b["why"]}
                              for b, x in zip(ip.BATTLES, ARENA_X)],
                  "plan": ip.plan_rounds(t)},
    }


# ------------------------------------------------------------------- проверка

def verify(path: Path, t: sq.Tables) -> None:
    """Прочитать записанный файл обратно и проверить числами всё, что генератор обещал."""
    scen = sg.Sg.read(path)
    types = {r.obj_id.upper(): sg.fields(r.body, sg.LAYOUT_UNIT)["TYPE"].lower()
             for r in scen.find(sg.WHAT_UNIT)}
    players = [sg.fields(r.body, sg.LAYOUT_PLAYER) for r in scen.find(sg.WHAT_PLAYER)]
    playable = [p for p in players if p["RACE_ID"] != sc.RACE_NEUTRAL]
    assert len(playable) == 1, "играбельных игроков %d" % len(playable)

    stacks = {r.obj_id: sg.parse_stack(r.body) for r in scen.find(sg.WHAT_STACK)}
    own = [s for s in stacks.values() if s["OWNER"] == playable[0]["PLAYER_ID"]]
    assert len(own) == 1, "своих отрядов %d" % len(own)
    st = own[0]
    members = {k: types[st[f"UNIT_{k}"].upper()] for k in range(6) if st[f"UNIT_{k}"] != sg.NULL_ID}
    got = [types[st[f"UNIT_{k}"].upper()] for k in range(1, 6) if st[f"UNIT_{k}"] != sg.NULL_ID]
    assert got == ip.SQUAD, "состав отряда %s, ожидался %s" % (got, ip.SQUAD)
    cells = {k: st[f"POS_{k}"] for k in range(6)}
    assert cells[ip.LEADER_CELL] == 0, "лидер не в клетке %d: строй %s" % (ip.LEADER_CELL, cells)
    front = [members[cells[c]] for c in (0, 2, 4) if cells[c] >= 0]
    assert ip.HERO_TYPE not in front, "лидер оказался в переднем ряду — его достанет ближний бой"
    assert all(t.is_melee(u) for u in front), "в переднем ряду не только ближники: %s" % front

    kinds = item_types(scen)
    inv = [kinds[i] for i in st["ITEMS"]]
    assert Counter(inv) == Counter(ip.INVENTORY), "инвентарь лидера %s, ожидался %s" % (
        sorted(Counter(inv).items()), sorted(Counter(ip.INVENTORY).items()))
    equipped = [st[s] for s in SLOTS if st[s] != sg.NULL_ID]
    assert not equipped, "боевые слоты заняты (%s) — их раскладывает пользователь" % equipped
    charges = parse_charges(scen.one(WHAT_TALISMAN).body)
    want_t = [i for i in st["ITEMS"] if kinds[i] == ip.ITEM_TALISMAN]
    assert sorted(i for i, _ in charges) == sorted(want_t), \
        "заряды талисманов записаны для %s, а талисманы это %s" % (charges, want_t)
    assert all(n == ip.TALISMAN_CHARGES for _, n in charges), "заряды %s" % charges

    foes = [s for s in stacks.values() if s["OWNER"] != playable[0]["PLAYER_ID"]]
    assert len(foes) == len(ip.BATTLES), "чужих отрядов %d" % len(foes)
    by_pos = {(int(s["POS_X"]), int(s["POS_Y"])): s for s in foes}
    for b, x in zip(ip.BATTLES, ARENA_X):
        s = by_pos[(x, ARENA_Y)]
        assert types[s["LEADER_ID"].upper()] == b["leader"], \
            "%s: лидер %s, ожидался %s" % (b["name"], types[s["LEADER_ID"].upper()], b["leader"])
        soldiers = [types[s[f"UNIT_{k}"].upper()] for k in range(1, 6)
                    if s[f"UNIT_{k}"] != sg.NULL_ID]
        assert soldiers == list(b["soldiers"]), \
            "%s: солдаты %s, ожидалось %s" % (b["name"], soldiers, b["soldiers"])

    ids = {r.obj_id for r in scen.records}
    _, plan = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    dangling = sorted({e for _, _, e in plan} - ids)
    assert not dangling, "в плане ссылки на удалённые записи: %s" % dangling
    for r in scen.find(sg.WHAT_STACK):
        s = sg.parse_stack(r.body)
        for ref in [s[f"UNIT_{k}"] for k in range(6)] + list(s["ITEMS"]):
            assert ref == sg.NULL_ID or ref in ids, "%s: ссылка на удалённую запись %s" % (r.obj_id, ref)
    print("проверка чтения: игрок, строй отряда, %d предметов у лидера, заряды талисманов, "
          "три боя и план сошлись" % len(inv))


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--template", type=Path, default=c4.TEMPLATE)
    p.add_argument("--out", type=Path, default=OUT)
    p.add_argument("--route", type=Path, default=ROUTE_OUT)
    p.add_argument("--name", default="items")
    p.add_argument("--plan", action="store_true", help="только расчёт, ничего не записывать")
    p.add_argument("--selftest", action="store_true", help="проверить сторожей подсадной ошибкой")
    a = p.parse_args(argv)
    if a.selftest:
        return ip.selftest()
    t = sq.Tables()
    ip.print_plan(t)
    if a.plan:
        return 0
    if not a.template.exists():
        raise SystemExit("нет шаблона %s" % a.template)
    print()
    scen, stats = build(a.template, a.name)
    a.out.parent.mkdir(parents=True, exist_ok=True)
    scen.write(a.out)
    print("%s: %s" % (a.out, ", ".join("%s %s" % kv for kv in stats.items())))
    verify(a.out, t)
    route = build_route(a.out)
    a.route.write_text(json.dumps(route, ensure_ascii=False, indent=2), encoding="utf-8")
    print("маршрут %s: ходов %d, боёв %d" % (a.route, len(route["turns"]), len(ip.BATTLES)))
    for turn in route["turns"]:
        acts = ", ".join(x["do"] + (":" + x["kind"] if x.get("kind") else "")
                         for x in turn.get("actions", []))
        print("  ход %d -> %s%s" % (turn["turn"], tuple(turn["target"]),
                                    ("  " + acts) if acts else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
