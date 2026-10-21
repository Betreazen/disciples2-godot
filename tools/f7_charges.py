#!/usr/bin/env python3
"""Карта M17 `f7-m17-charges`: расход зарядов талисмана, сферы и посоха — правила, числа,
таблицы гипотез и сторожа. Игра отсюда не запускается; байты карты собирает
`tools/make_f7_m17_map.py`.

Ранги: **Р1** — таблицы игры, живой журнал/сохранение или дизассемблер по адресу (GOG 3.01,
ImageBase 0x400000); **Р2** — чужой реверс; **Р3** — гипотеза, её и меряет карта.

**Талисман (`ITEM_CAT` 12).**
* Надеть — способность 15 `L_TALISMAN_USE` (гейт `0x5DF6B7`, код 6). Р1.
* Применение — действие героя в бою; предмет, уже применённый в ЭТОМ бою, второй раз не
  предлагается (`BattleMsgData.usedItemIds[4]`, проверка — `battlemsgdatahooks.cpp:285`, Р2;
  обход четырёх id по `+0xF20` — `0x622FEE`, Р1).
* Заряд списывается ПОСЛЕ боя: `checkAndDestroyEquippedBattleItems` `0x629755` обходит слоты
  BATTLE1/BATTLE2 обоих предводителей, `0x629824` берёт только вещи из `usedItemIds`; для
  талисмана визитор `0x5E74BB` → apply `0x610FC6` → `0x5EBB30` → `sub [запись+4], 1`
  (`0x5EBB49`). То есть «за бой, в котором применён», а не за раунд и не за ношение. Р1.
  Живьём: M8, сохранение t04 после боя с одним применением — `S143IM0004 = 4`. Р1.
* На нуле: сразу после списания `0x5EB9B3` читает остаток; `< 1` → `unEquipItem 0x5E746D` и
  `destroyItem 0x5E737D` — вещь исчезает. Применить с нулём нельзя: canApply `0x610F45`
  требует `≥ 1`. Р1 по коду, живьём не видено.
* Старт 5 зарядов — СТАТИКА `[0x6EA178] = 5` при создании (`0x5EB9CA`), а не `GVars.TALIS_CHRG`
  (тот читается в `GlobalVariables+0x128`, `0x586E73`, и в пути создания не участвует). Р1.
* Прибавки нет нигде: писателей записи два — создание и `0x5EBB30`; второй вызывающий
  `0x5EBB30` — визитор «выставить N» `0x611087`, его зовёт одно место `0x42F8C6` (эффект
  карты, не суточный тик). Р1 на перечень, Р3 на то, что это за эффект.

**Сфера (`ITEM_CAT` 11).** Способность 14 `L_ORB_USE` (у Архимага врождённая). После боя, в
котором применена, снимается и уничтожается (`0x629824`, ветка сферы и четырёх зелий). Р1;
живьём: M8, `item_unequip` на конце боя и пропажа записи `IM`. Надетая без применения — цела
(та же проверка `usedItemIds`). Р1 по коду.

**Посох (`ITEM_CAT` 9).** Применяется на КАРТЕ, не в бою; способность 7 `L_WAND_SCROLL_USE`
(`X005TA0591`). Проверка `0x5DBBE8(itemId, stackId, objectMap)`: предмет, отряд, живой герой,
мана ≥ `GItem.CASTING` (иначе код 9), затем счётчик применений. Счётчик —
`CMidSpellCast`, ВТОРОЙ список `+0x18` (первый `+8` — заклинания), ключ **(игрок, id предмета
`IM`)**: `0x606F50` → `0x606ECA` считает совпадения. Лорд категории `L_MAGE` (`[0x837760]`,
регистрация `LLord.dbf` `0x585FA0`) — до двух в сутки (код 0x12, `X005TA0580`), прочие — один
(код 0x11, `X005TA0579`). Вставка во второй список — `0x607012` (визитор `0x61372F`). Р1.
Зарядов у посоха нет, цена — мана `CASTING` за каждое применение (`Item.cs`, Р1 по данным).
Что посох не расходуется — Р3. Суточный сброс — Р2 (у заклинаний `TURN_STOP` = ход).
"""
from __future__ import annotations

import functools
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import arena_plan as ap  # noqa: E402      (Numbers: dpr/hp/heal по Gunits+Gattacks)
import make_c4_map as c4  # noqa: E402
import scenario_squads as sq  # noqa: E402
from dbf_dump import read as dbf_read  # noqa: E402
from f7_wave2 import (  # noqa: E402
    CAP, HP_MARGIN, MAP_SIZE, MOVE_RESERVE, NEAR_CAPITAL, PLAIN_COST, RACE_EMPIRE, cheb, grace)

# ------------------------------------------------------------------ состав

LEADER = "g000uu0021"        # Архимаг: врождённо 7 (посохи) и 14 (сферы), GLabi
CAT_MAGE = 3
MOD_TOMES = "G000UM4002"     # «Тайное знание», GmodifL TYPE 11 ABILITY 10: без него Книгу не надеть
SQUIRE = "g000uu0001"        # передний ряд: 100 HP, 25 урона
PRIEST = "g000uu0012"        # задний ряд: лечит 40 — отряд идёт восемь дней без Храма
DWARF = "g000uu5236"         # нейтрал цепочки: 150 HP, 30 урона, ближний, без иммунитетов
DUMMY = "g000uu5370"         # мишень посохов: 600 HP, броня 0 (урон читается без поправки)

TALISMAN = "G000IG9137"      # те же id, что f7_plan.ITEM_*: сверяет make_f7_m17_map
ORB = "G000IG9041"
BOOK = "G000IG4007"
WAND_A = "G000IG6001"        # g000ss0004 L_ATTACK, земля 15, мана жизни 100
WAND_B = "G000IG6011"        # g000ss0072 L_ATTACK, смерть 50, мана смерти (E) и рун (W) по 150
START_CHARGES = 5            # статика 0x6EA178
LORD_WARRIOR, LORD_MAGE = "g000LR0001", "g000LR0003"

CAT_JEWEL, CAT_WAND, CAT_ORB, CAT_TALISMAN = 1, 9, 11, 12
ABILITY_FOR = {0: 8, 1: 10, 2: 8, 3: 9, 8: 7, 9: 7, 11: 14, 12: 15, 13: 16}   # гейт 0x5DF6B7
DAMAGE_CLASSES = (1, 2, 15)

INSTANCES = {"T1": TALISMAN, "T2": TALISMAN, "O1": ORB, "O2": ORB,
             "A": WAND_A, "A2": WAND_A, "B": WAND_B}
TALISMANS, ORBS, WANDS = ("T1", "T2"), ("O1", "O2"), ("A", "A2", "B")

# --------------------------------------------------------------- геометрия

ENTRANCE = (CAP[0] + c4.CAPITAL_SIZE - 1, CAP[1] + c4.CAPITAL_SIZE - 1)   # (9,9)
START = NEAR_CAPITAL                                                       # (10,10)
DUMMY_XY = (7, 15)
CHAIN = [(13, 15), (17, 12), (14, 8), (12, 5), (16, 4), (20, 6), (24, 8)]

# Бой k: что в слотах BATTLE1/BATTLE2 и что применяется в каком раунде.
BATTLES = [
    {"slots": ("T1", "O1"), "apply": (("T1", 1), ("T1", 2))},   # второе применение — проба
    {"slots": ("T1", "O1"), "apply": (("O1", 1),)},             # талисман в слоте БЕЗ применения
    {"slots": ("T1", "O2"), "apply": (("T1", 1), ("O2", 2))},   # вторая сфера — повтор
    {"slots": ("T1", None), "apply": (("T1", 1),)},
    {"slots": ("T1", None), "apply": (("T1", 1),)},
    {"slots": ("T1", None), "apply": (("T1", 1),)},             # по Р1 здесь T1 кончается
    {"slots": ("T1", "T2"), "apply": (("T1", 1), ("T2", 2))},   # сверх: T1 уже нет
]

# День: путь (клетки-вехи), кого бьёт (индекс цепочки) и откуда колдует посохами.
ROUTE = [
    {"path": (START, (10, 13), (12, 14)), "attack": 0, "cast_at": (10, 13)},
    {"path": ((12, 14), (16, 13)), "attack": 1, "cast_at": (12, 14)},
    {"path": ((16, 13), (15, 9)), "attack": 2},
    {"path": ((15, 9), ENTRANCE), "attack": None},               # ночь в столице
    {"path": (ENTRANCE, (11, 6)), "attack": 3},
    {"path": ((11, 6), (15, 5)), "attack": 4},
    {"path": ((15, 5), (19, 6)), "attack": 5},
    {"path": ((19, 6), (23, 7)), "attack": 6},
]

TIMELINE = (
    ("cast", "A"), ("cast", "A"), ("cast", "A2"), ("cast", "B"), ("save", "W1"),
    ("battle", 0), ("save", "Б1"), ("night", "поле"),
    ("save", "У2"), ("cast", "A"), ("save", "W2"), ("battle", 1), ("save", "Б2"),
    ("night", "поле"),
    ("battle", 2), ("save", "Б3"), ("night", "поле"),
    ("night", "столица"),
    ("save", "Г5"), ("battle", 3), ("save", "Б4"), ("night", "поле"),
    ("battle", 4), ("save", "Б5"), ("night", "поле"),
    ("battle", 5), ("save", "Б6"), ("night", "поле"),
    ("battle", 6), ("save", "Б7"),
)


def _neutral(xy, leader, why, min_rounds=1, fight=True) -> dict:
    return {"xy": xy, "leader": leader, "soldiers": (), "why": why,
            "min_rounds": min_rounds, "fight": fight, "poison": False}


def _min_rounds(battle: dict) -> int:
    """Бой обязан дожить до последнего действия героя и ещё раунд — ради «за раунд»."""
    return max(r for _, r in battle["apply"]) + 1


PROFILE = {
    "file": "f7_m17_charges.sg", "name": "f7-m17-charges",
    "why": "S12+: расход зарядов талисмана, сферы и посоха — диффом сохранений",
    "bank": "G1000:R0000:Y1000:E1000:W1000:B0000",
    "parties": [{"xy": START, "leader": LEADER, "cat": CAT_MAGE,
                 "units": [(SQUIRE, None), (SQUIRE, None), (PRIEST, None)],
                 "why": "Архимаг прогона: сферы и посохи врождённо, «Тайное знание» модификатором",
                 "cell": 1, "fights": True, "leader_hp": None, "modifiers": [MOD_TOMES]}],
    "bags": [{"xy": (10, 11), "items": [BOOK, TALISMAN, TALISMAN], "why": "Книга и два талисмана"},
             {"xy": (10, 12), "items": [ORB, ORB], "why": "две сферы: расход и повтор"},
             {"xy": (10, 13), "items": [WAND_A, WAND_A, WAND_B], "why": "два одинаковых посоха и третий"}],
    "need_items": [BOOK, TALISMAN, ORB, WAND_A, WAND_B],
    "neutrals": [_neutral(xy, DWARF, "бой %d цепочки" % (k + 1), _min_rounds(BATTLES[k]))
                 for k, xy in enumerate(CHAIN)]
                + [_neutral(DUMMY_XY, DUMMY, "мишень посохов: не драться", fight=False)],
    "charges": {"battles": BATTLES, "route": ROUTE, "timeline": TIMELINE, "lord": LORD_WARRIOR},
    "turns": len(ROUTE),
    "spells": [], "village": None, "rods": {}, "need_dead": False,
}

# ------------------------------------------------------------ чтение таблиц


@functools.lru_cache(maxsize=None)
def _items() -> dict:
    return {r["ITEM_ID"].upper(): r for r in dbf_read(sq.GLOBALS / "GItem.DBF")["rows"]}


def item_cat(item_id: str) -> int:
    return int(_items()[item_id.upper()]["ITEM_CAT"])


@functools.lru_cache(maxsize=None)
def spell(spell_id: str) -> dict:
    for r in dbf_read(sq.GLOBALS / "Gspells.dbf")["rows"]:
        if r["SPELL_ID"].lower() == spell_id.lower():
            return r
    raise SystemExit("заклинания %s нет в Gspells.dbf" % spell_id)


@functools.lru_cache(maxsize=None)
def innate(unit_id: str) -> frozenset:
    return frozenset(int(r["L_ABILITY"]) for r in dbf_read(sq.GLOBALS / "GLabi.dbf")["rows"]
                     if r["UNIT_ID"].lower() == unit_id.lower())


@functools.lru_cache(maxsize=None)
def granted(mod_id: str) -> frozenset:
    return frozenset(int(r["ABILITY"]) for r in dbf_read(sq.GLOBALS / "GmodifL.dbf")["rows"]
                     if r["BELONGS_TO"].lower() == mod_id.lower() and int(r["TYPE"]) == 11)


def bank(text: str) -> dict[str, int]:
    """`G1000:R0000:…` и `g0000:r0000:y0100:…` — в словарь по заглавной букве."""
    return {p[0].upper(): int(p[1:]) for p in text.split(":") if p}


def wand_cost(key: str) -> dict[str, int]:
    return bank(_items()[INSTANCES[key]]["CASTING"])


def wand_spell(key: str) -> dict:
    return spell(_items()[INSTANCES[key]]["SPELL_ID"])


def fighter(prof: dict) -> dict:
    return next(p for p in prof["parties"] if p["fights"])


def chain(prof: dict) -> list[dict]:
    return [n for n in prof["neutrals"] if n["fight"]]


def dummy(prof: dict) -> dict:
    found = [n for n in prof["neutrals"] if not n["fight"]]
    if len(found) != 1:
        raise SystemExit("m17: мишеней посохов %d, нужна ровно одна" % len(found))
    return found[0]


# ---------------------------------------------------------------- гейт и вещи

def check_gate(prof: dict, key: str) -> None:
    """Каждую категорию из мешков лидер обязан уметь — книга сама требует способность 10."""
    party = fighter(prof)
    base = set(innate(party["leader"]))
    for m in party.get("modifiers", ()):
        base |= granted(m)
    have = set(base)
    items = [i for b in prof["bags"] for i in b["items"]]
    for i in (i for i in items if item_cat(i) == CAT_JEWEL):
        if ABILITY_FOR[CAT_JEWEL] not in base:
            raise SystemExit("%s: Книгу %s не надеть — у лидера нет способности 10 (Тайное "
                             "знание), а только %s; талисман останется закрыт"
                             % (key, i, sorted(base)))
        have |= granted(_items()[i.upper()]["MOD_EQUIP"])
    for i in items:
        need = ABILITY_FOR.get(item_cat(i))
        if need is not None and need not in have:
            raise SystemExit("%s: лидер %s не умеет категорию %d (%s) — нужна способность %d, "
                             "есть %s" % (key, party["leader"], item_cat(i), i, need, sorted(have)))


def check_items(prof: dict, key: str) -> None:
    """Два талисмана, две сферы, два одинаковых посоха и третий с другим заклинанием."""
    got = Counter(i.upper() for b in prof["bags"] for i in b["items"])
    for kind, cat, need in (("талисмана", CAT_TALISMAN, 2), ("сферы", CAT_ORB, 2),
                            ("посоха", CAT_WAND, 3)):
        have = sum(n for i, n in got.items() if item_cat(i) == cat)
        if have < need:
            raise SystemExit("%s: %s в мешках %d при нужных %d — без второго экземпляра повтор "
                             "не проверить" % (key, kind, have, need))
    for want in (BOOK, TALISMAN, ORB):
        if not got[want]:
            raise SystemExit("%s: в мешках нет %s" % (key, want))
    wands = [i for i in got if item_cat(i) == CAT_WAND]
    spells = {_items()[i]["SPELL_ID"].lower() for i in wands}
    if not any(got[i] >= 2 for i in wands) or len(spells) < 2:
        raise SystemExit("%s: посохи %s — нужны два ОДИНАКОВЫХ и третий с другим заклинанием"
                         % (key, dict((i, got[i]) for i in wands)))


# ----------------------------------------------------------------- бой

def _attack_qty(n: ap.Numbers, unit_id: str) -> int:
    a = n.attack(unit_id)
    return int(a["QTY_DAM"] or 0) if a and int(a["CLASS"]) in DAMAGE_CLASSES else 0


def _tick(t: sq.Tables, key: str, foes: int) -> tuple[float, float]:
    """(наибольший, ожидаемый) урон статуса предмета за раунд по всему чужому строю."""
    a = t.attacks[_items()[INSTANCES[key]]["ATTACK_ID"].lower()]
    width = foes if int(a["REACH"]) == 1 else 1
    qty = int(a["QTY_DAM"] or 0) * width
    return float(qty), qty * int(a["POWER"]) / 100.0


def battle_model(t: sq.Tables, prof: dict, k: int) -> dict:
    """Бой k в двух оценках. НАИХУДШАЯ: каждый удар попал, статус тикает с раунда применения —
    враг обязан пережить последнее действие героя. ОЖИДАЕМАЯ: урон с точностью — число раундов.
    В раунд, где герой применяет вещь, он не бьёт."""
    n, party = ap.Numbers(t), fighter(prof)
    plan, foe = prof["charges"]["battles"][k], chain(prof)[k]
    soldiers = [u for u, _ in party["units"]]
    foes = [foe["leader"]] + list(foe["soldiers"])
    hp = sum(n.effective_hp(u) for u in foes)
    s_max = float(sum(_attack_qty(n, u) for u in soldiers))
    s_exp = sum(n.dpr(u, targets=len(foes)) for u in soldiers)
    reach_all = int(n.attack(party["leader"])["REACH"]) == 1
    l_max = _attack_qty(n, party["leader"]) * (len(foes) if reach_all else 1)
    l_exp = n.dpr(party["leader"], targets=len(foes))
    acts = dict((r, key) for key, r in plan["apply"])
    last = max(acts)
    worst = exp = 0.0
    rounds = 0
    while exp < hp and rounds < 50:
        rounds += 1
        ticks = [_tick(t, key, len(foes)) for key in {kk for kk, r in plan["apply"] if r <= rounds}]
        idle = rounds not in acts
        if rounds <= last:
            worst += s_max + sum(a for a, _ in ticks) + (l_max if idle else 0)
        exp += s_exp + sum(b for _, b in ticks) + (l_exp if idle else 0)
    their = sum(n.dpr(u, targets=1 + len(soldiers)) for u in foes)
    return {"бой": k + 1, "HP врага": round(hp), "наихудший урон к действию": round(worst),
            "последнее действие": last, "раундов": rounds, "их урон/раунд": round(their),
            "лечение/раунд": sum(n.heal(u) for u in soldiers) / 2.0,
            "передний ряд": sum(n.hp(u) for u in soldiers if t.is_melee(u)),
            "реген за ночь": sum(n.regen(u) for u in soldiers if t.is_melee(u))}


def battle_rows(t: sq.Tables, prof: dict) -> list[dict]:
    plan = prof["charges"]["battles"]
    if len(plan) != len(chain(prof)):
        raise SystemExit("m17: боёв в плане %d, нейтралов цепочки %d — план и карта разошлись"
                         % (len(plan), len(chain(prof))))
    return [battle_model(t, prof, k) for k in range(len(plan))]


def check_battles(t: sq.Tables, prof: dict, key: str, rows: list[dict] | None = None) -> None:
    """Каждый бой не короче двух раундов и доживает до последнего действия героя."""
    for r in battle_rows(t, prof) if rows is None else rows:
        need = max(2, r["последнее действие"] + 1)
        if r["наихудший урон к действию"] >= r["HP врага"]:
            raise SystemExit("%s бой %d: в худшем случае к раунду %d враг получит %d при %d HP — "
                             "герой не успеет применить вещь" % (key, r["бой"],
                                                                  r["последнее действие"],
                                                                  r["наихудший урон к действию"],
                                                                  r["HP врага"]))
        if r["раундов"] < need:
            raise SystemExit("%s бой %d: %d раундов при нужных %d — «за раунд» и «за бой» не "
                             "различить" % (key, r["бой"], r["раундов"], need))


def nights_after(timeline: tuple, k: int) -> int:
    """Сколько ночей между боем k и следующим боем."""
    at = timeline.index(("battle", k))
    nxt = next((i for i, e in enumerate(timeline) if e == ("battle", k + 1)), len(timeline))
    return sum(1 for e in timeline[at:nxt] if e[0] == "night")


def check_attrition(t: sq.Tables, prof: dict, key: str, rows: list[dict] | None = None) -> None:
    """Отряд не гибнет ни в бою, ни от накопленных потерь; реген без бонуса лорда."""
    rows = battle_rows(t, prof) if rows is None else rows
    front = rows[0]["передний ряд"]
    hp = float(front)
    for k, r in enumerate(rows):
        raw = r["их урон/раунд"] * r["раундов"]
        if raw > front * (1.0 - HP_MARGIN):
            raise SystemExit("%s бой %d: потери %d при переднем ряде %d — отряд ляжет"
                             % (key, r["бой"], raw, front))
        hp -= max(0.0, r["их урон/раунд"] - r["лечение/раунд"]) * r["раундов"]
        if hp <= front * HP_MARGIN:
            raise SystemExit("%s бой %d: накопленные потери оставят переднему ряду %d из %d"
                             % (key, r["бой"], hp, front))
        hp = min(float(front), hp + r["реген за ночь"] * nights_after(
            prof["charges"]["timeline"], k))


def check_immunity(t: sq.Tables, prof: dict, key: str) -> None:
    """Предметные атаки ложатся на нейтралов, заклинания посохов — на мишень."""
    by_src, by_cls = ap.immunities(t)
    for n in chain(prof):
        for item in (TALISMAN, ORB):
            a = t.attacks[_items()[item]["ATTACK_ID"].lower()]
            if (int(a["SOURCE"]) in by_src.get(n["leader"].lower(), set())
                    or int(a["CLASS"]) in by_cls.get(n["leader"].lower(), set())):
                raise SystemExit("%s: нейтрал %s иммунен к атаке %s — применение пройдёт вхолостую"
                                 % (key, n["leader"], item))
    d = dummy(prof)["leader"].lower()
    for w in WANDS:
        src = int(wand_spell(w)["DAMAGE_SRC"])
        if src in by_src.get(d, set()):
            raise SystemExit("%s: мишень %s иммунна к источнику %d посоха %s" % (key, d, src, w))


# --------------------------------------------------------------- маршрут

def cells(path: tuple) -> list[tuple[int, int]]:
    """Клетки пути по вехам, шагами короля."""
    out = [tuple(path[0])]
    for a, b in zip(path, path[1:]):
        x, y = a
        while (x, y) != tuple(b):
            x += (b[0] > x) - (b[0] < x)
            y += (b[1] > y) - (b[1] < y)
            out.append((x, y))
    return out


def check_route(t: sq.Tables, prof: dict, key: str) -> None:
    """Дни сцеплены, шаги оплачены с запасом, мешки на пути первого дня, пути свободны."""
    route, foes = prof["charges"]["route"], chain(prof)
    party = fighter(prof)
    budget = int(t.unit(party["leader"])["MOVE"]) * MOVE_RESERVE
    blocked = {tuple(n["xy"]) for n in prof["neutrals"]}      # побеждённый отряд с карты уходит
    at = tuple(party["xy"])
    for day, d in enumerate(route, 1):
        walk = cells(d["path"])
        if day > 1 and route[day - 2]["attack"] is not None:
            blocked.discard(tuple(foes[route[day - 2]["attack"]]["xy"]))
        if walk[0] != at:
            raise SystemExit("%s день %d: путь начинается в %s, а отряд стоит в %s"
                             % (key, day, walk[0], at))
        cost = PLAIN_COST * (len(walk) - 1 + (d["attack"] is not None))
        if cost > budget:
            raise SystemExit("%s день %d: путь стоит %d очков при запасе %.0f" % (key, day, cost, budget))
        if blocked & set(walk):
            raise SystemExit("%s день %d: путь проходит через отряд в %s" % (key, day, blocked & set(walk)))
        if d["attack"] is not None and cheb(walk[-1], tuple(foes[d["attack"]]["xy"])) != 1:
            raise SystemExit("%s день %d: из %s нейтрала %s не достать"
                             % (key, day, walk[-1], foes[d["attack"]]["xy"]))
        at = walk[-1]
    missing = [b["xy"] for b in prof["bags"] if tuple(b["xy"]) not in cells(route[0]["path"])]
    if missing:
        raise SystemExit("%s: мешки %s не лежат на пути первого дня" % (key, missing))
    check_city_night(prof, key)


def check_city_night(prof: dict, key: str) -> None:
    """Ночь «столица» стоит после дня, чей путь кончается во входе столицы."""
    tl, route = prof["charges"]["timeline"], prof["charges"]["route"]
    night = 0
    for kind, arg in tl:
        if kind != "night":
            continue
        night += 1
        ends = cells(route[night - 1]["path"])[-1] == ENTRANCE
        if (arg == "столица") != ends:
            raise SystemExit("%s: ночь %d «%s», а путь дня %d кончается %s"
                             % (key, night, arg, night, "во входе столицы" if ends else "в поле"))


def check_dummy(t: sq.Tables, prof: dict, key: str) -> None:
    """Мишень видна из точек колдовства, переживает худший случай, мана не кончится."""
    d, party = dummy(prof), fighter(prof)
    scout = int(t.unit(party["leader"])["SCOUT"])
    for day, r in enumerate(prof["charges"]["route"], 1):
        if "cast_at" in r and cheb(tuple(r["cast_at"]), tuple(d["xy"])) > scout:
            raise SystemExit("%s день %d: мишень %s дальше обзора %d от %s"
                             % (key, day, d["xy"], scout, r["cast_at"]))
    worst = sim_wands(t, prof, "без предела")
    if min(v[3] for v in worst.values()) <= 0:
        raise SystemExit("%s: мишень посохов погибнет от худшего случая" % key)
    if min(min(v[:3]) for v in worst.values()) < 0:
        raise SystemExit("%s: маны не хватит на худший случай — отказ по мане спутает счётчик" % key)


# ------------------------------------------------------------- гипотезы

SPEND = ("бой с применением", "применение", "раунд", "бой в слоте")
ZERO = ("исчезает", "остаётся")
RECHARGE = ("никогда", "+1 в сутки", "в городе")
ORB_RULES = ("бой с применением", "бой в слоте", "не расходуется")
WAND_RULES = ("раз в сутки на посох", "раз в сутки на заклинание", "раз в сутки на героя",
              "без предела", "одноразовый", "без суточного сброса")


def _spend(ch: dict, key: str, battle: dict, rounds: int, rule: str) -> None:
    tries = sorted(r for kk, r in battle["apply"] if kk == key)
    if rule == "бой в слоте":
        take = 1
    elif not tries:
        take = 0
    elif rule == "применение":
        take = len(tries)
    elif rule == "раунд":
        take = max(1, rounds - tries[0] + 1)
    else:
        take = 1
    ch[key] = max(0, ch[key] - min(take, ch[key]))


def sim_talisman(prof: dict, rounds: list[int], spend: str = SPEND[0], zero: str = ZERO[0],
                 recharge: str = RECHARGE[0]) -> dict[str, tuple]:
    ch: dict[str, int | None] = {k: START_CHARGES for k in TALISMANS}
    out = {}
    plan = prof["charges"]["battles"]
    for kind, arg in prof["charges"]["timeline"]:
        if kind == "battle":
            for key in (k for k in TALISMANS if k in plan[arg]["slots"] and ch[k]):
                _spend(ch, key, plan[arg], rounds[arg], spend)
                if ch[key] == 0 and zero == "исчезает":
                    ch[key] = None
        elif kind == "night":
            for key in (k for k in TALISMANS if ch[k] is not None):
                if recharge == "+1 в сутки" or (recharge == "в городе" and arg == "столица"):
                    ch[key] = START_CHARGES if recharge == "в городе" else min(START_CHARGES, ch[key] + 1)
        elif kind == "save" and arg.startswith(("Б", "У", "Г")):
            out[arg] = tuple(ch[k] for k in TALISMANS)
    return out


def sim_orbs(prof: dict, rule: str) -> dict[str, tuple]:
    have, out = set(ORBS), {}
    plan = prof["charges"]["battles"]
    for kind, arg in prof["charges"]["timeline"]:
        if kind == "battle":
            worn = {k for k in plan[arg]["slots"] if k in have}
            used = {k for k, _ in plan[arg]["apply"] if k in worn}
            have -= used if rule == "бой с применением" else worn if rule == "бой в слоте" else set()
        elif kind == "save" and arg in ("Б1", "Б2", "Б3"):
            out[arg] = (len(have),)
    return out


def _wand_key(rule: str, key: str) -> str:
    if rule == "раз в сутки на заклинание":
        return wand_spell(key)["SPELL_ID"].lower()
    return "герой" if rule == "раз в сутки на героя" else key


def sim_wands(t: sq.Tables, prof: dict, rule: str) -> dict[str, tuple]:
    """(мана жизни, смерти, огня, HP мишени, посохов в отряде) в сохранениях W1, W2.
    Лимит «на посох» под лордом-Магом — два, иначе один (0x5DBD17). Мана и доход — с тиком
    уже при загрузке; реген мишени ночью — допущение (у W2 HP сдвинут на него)."""
    income = bank(grace(RACE_EMPIRE)["INCOME"])
    purse = {c: bank(prof["bank"]).get(c, 0) + income.get(c, 0) for c in "YEW"}
    row = t.unit(dummy(prof)["leader"])
    hp_max, regen = int(row["HIT_POINT"]), int(row["REGEN"] or 0)
    hp, have, used, out = hp_max, set(WANDS), Counter(), {}
    limit = 2 if prof["charges"]["lord"] == LORD_MAGE else 1
    for kind, arg in prof["charges"]["timeline"]:
        if kind == "night":
            purse = {c: purse[c] + income.get(c, 0) for c in purse}
            used = used if rule == "без суточного сброса" else Counter()
            hp = min(hp_max, hp + hp_max * regen // 100)
        elif kind == "cast":
            k = _wand_key(rule, arg)
            if arg in have and (rule in ("без предела", "одноразовый") or used[k] < limit):
                purse = {c: purse[c] - wand_cost(arg).get(c, 0) for c in purse}
                hp -= int(wand_spell(arg)["DAMAGE_QTY"] or 0)
                used[k] += 1
                if rule == "одноразовый":
                    have.discard(arg)
        elif kind == "save" and arg.startswith("W"):
            out[arg] = (purse["Y"], purse["E"], purse["W"], hp, len(have))
    return out


def tables(t: sq.Tables, prof: dict) -> dict[str, dict[str, dict]]:
    """Пять таблиц: по одной оси за раз, остальные оси — на чтении Р1."""
    rounds = [r["раундов"] for r in battle_rows(t, prof)]
    return {
        "талисман: как тратится": {h: sim_talisman(prof, rounds, spend=h) for h in SPEND},
        "талисман: что на нуле": {h: sim_talisman(prof, rounds, zero=h) for h in ZERO},
        "талисман: восстановление": {h: sim_talisman(prof, rounds, recharge=h) for h in RECHARGE},
        "сфера": {h: sim_orbs(prof, h) for h in ORB_RULES},
        "посох": {h: sim_wands(t, prof, h) for h in WAND_RULES},
    }


def check_distinct(table: dict[str, dict], title: str, key: str) -> None:
    """Любые две гипотезы обязаны разойтись хотя бы в одном сохранении."""
    if len(table) < 2 or not all(table.values()):
        raise SystemExit("%s: таблица «%s» пуста — сравнивать нечего" % (key, title))
    seen: dict[tuple, str] = {}
    for name, saves in table.items():
        vec = tuple(saves[s] for s in sorted(saves))
        if vec in seen:
            raise SystemExit("%s: «%s» — гипотезы «%s» и «%s» дают одни и те же числа во всех "
                             "сохранениях; перестрой сценарий" % (key, title, seen[vec], name))
        seen[vec] = name


def check_exhausts(t: sq.Tables, prof: dict, key: str) -> None:
    """По Р1 талисман T1 обязан кончиться, и после этого обязан быть ещё бой с попыткой."""
    rounds = [r["раундов"] for r in battle_rows(t, prof)]
    saves = sim_talisman(prof, rounds)
    battles = [s for s in saves if s.startswith("Б")]
    gone = next((s for s in battles if saves[s][0] is None), None)
    if gone is None:
        raise SystemExit("%s: за %d боёв T1 не опустел (в конце %s зарядов) — нейтралов или "
                         "применений не хватает" % (key, len(battles), saves[battles[-1]][0]))
    after = battles.index(gone) + 1
    plan = prof["charges"]["battles"]
    if not any(k == "T1" for b in plan[after:] for k, _ in b["apply"]):
        raise SystemExit("%s: T1 кончается в «%s», а сверх этого попытки нет — что на нуле, "
                         "не увидеть" % (key, gone))


def check_charges(t: sq.Tables, prof: dict, key: str) -> list[dict]:
    """Все сторожа M17 разом. Возвращает расчёт боёв для печати."""
    check_gate(prof, key)
    check_items(prof, key)
    rows = battle_rows(t, prof)
    check_battles(t, prof, key, rows)
    check_attrition(t, prof, key, rows)
    check_immunity(t, prof, key)
    check_route(t, prof, key)
    check_dummy(t, prof, key)
    check_exhausts(t, prof, key)
    for title, table in tables(t, prof).items():
        check_distinct(table, title, key)
    return rows


# ---------------------------------------------------------------- печать

LEGEND = {"талисман": "заряды T1/T2, «нет» = записи IM нет", "сфера": "сфер в отряде",
          "посох": "мана жизни Y/смерти E/рун W / HP мишени / посохов"}


def _fmt(v: tuple) -> str:
    return "/".join("нет" if x is None else str(x) for x in v)


def print_charges(t: sq.Tables, prof: dict, rows: list[dict] | None = None) -> None:
    rows = battle_rows(t, prof) if rows is None else rows
    for r in rows:
        print("   бой %d: HP врага %d, раундов %d, худший урон к раунду %d — %d, их урон %d/раунд"
              % (r["бой"], r["HP врага"], r["раундов"], r["последнее действие"],
                 r["наихудший урон к действию"], r["их урон/раунд"]))
    for day, d in enumerate(prof["charges"]["route"], 1):
        walk = cells(d["path"])
        print("   день %d: %s -> %s, шагов %d%s" % (day, walk[0], walk[-1], len(walk) - 1,
                                                  "" if d["attack"] is None
                                                  else ", бой %d" % (d["attack"] + 1)))
    order = [a for k, a in prof["charges"]["timeline"] if k == "save"]
    for title, table in tables(t, prof).items():
        saves = [s for s in order if s in next(iter(table.values()))]
        print("   %s (%s):" % (title, next(v for k, v in LEGEND.items() if title.startswith(k))))
        print("   %-26s %s" % ("гипотеза", " ".join("%-12s" % s for s in saves)))
        for name, vals in table.items():
            print("   %-26s %s" % (name, " ".join("%-12s" % _fmt(vals[s]) for s in saves)))


# --------------------------------------------------------------- selftest

def _with(prof: dict, **charges) -> dict:
    return dict(prof, charges=dict(prof["charges"], **charges))


def _party(prof: dict, **kw) -> dict:
    return dict(prof, parties=[dict(fighter(prof), **kw)])


def _chain_as(prof: dict, leader: str) -> dict:
    return dict(prof, neutrals=[dict(n, leader=leader) if n["fight"] else n
                                for n in prof["neutrals"]])


def _bags(prof: dict, drop: tuple = (), once: tuple = ()) -> dict:
    out = []
    for b in prof["bags"]:
        items = [i for i in b["items"] if i not in drop]
        for i in once:
            while items.count(i) > 1:
                items.remove(i)
        out.append(dict(b, items=items))
    return dict(prof, bags=out)


def _cut(prof: dict, keep: int) -> dict:
    """Цепочка из `keep` нейтралов: план и хронология обрезаны на тот же бой."""
    tl = []
    for e in prof["charges"]["timeline"]:
        if e[0] == "battle" and e[1] >= keep:
            break
        tl.append(e)
    return _with(dict(prof, neutrals=chain(prof)[:keep] + [dummy(prof)]),
                 battles=prof["charges"]["battles"][:keep], timeline=tuple(tl))


def _distinct(t: sq.Tables, prof: dict, title: str) -> None:
    check_distinct(tables(t, prof)[title], title, "m17")


def profile_cases(t: sq.Tables, prof: dict, raises) -> list[tuple[str, str | None]]:
    """Порча ровно одного обещания за раз. `raises` — ловилка из `f7_plan`."""
    plan, tl, route = (prof["charges"][k] for k in ("battles", "timeline", "route"))
    one_try = [dict(b, apply=b["apply"][:1]) if k == 0 else b for k, b in enumerate(plan)]
    no_carry = [dict(b, slots=("T1", None)) if k == 0 else b for k, b in enumerate(plan)]
    far_day = [dict(route[0], path=(START, (10, 13), (10, 20), (12, 14)))] + list(route[1:])
    return [
        ("лидер без «Тайного знания»", raises(lambda: check_gate(_party(prof, modifiers=[]), "m17"))),
        ("лидер не маг (Рыцарь на Пегасе)", raises(
            lambda: check_gate(_party(prof, leader="g000uu0019"), "m17"))),
        ("в мешках нет талисмана", raises(lambda: check_items(_bags(prof, drop=(TALISMAN,)), "m17"))),
        ("в мешках нет сферы", raises(lambda: check_items(_bags(prof, drop=(ORB,)), "m17"))),
        ("в мешках нет посохов", raises(
            lambda: check_items(_bags(prof, drop=(WAND_A, WAND_B)), "m17"))),
        ("одна сфера — повтор не проверить", raises(
            lambda: check_items(_bags(prof, once=(ORB,)), "m17"))),
        ("посохи без одинаковой пары", raises(lambda: check_items(dict(prof, bags=[
            dict(b, items=[WAND_A, "G000IG6003", WAND_B]) if WAND_B in b["items"] else b
            for b in prof["bags"]]), "m17"))),
        ("нейтралов не хватает опустошить талисман", raises(
            lambda: check_exhausts(t, _cut(prof, 5), "m17"))),
        ("бой короче двух раундов (Разбойник)", raises(
            lambda: check_battles(t, _chain_as(prof, "g000uu5130"), "m17"))),
        ("отряд гибнет по расчёту (Мантикора)", raises(
            lambda: check_attrition(t, _chain_as(prof, "g000uu6016"), "m17"))),
        ("«за применение» = «за бой»", raises(
            lambda: _distinct(t, _with(prof, battles=one_try), "талисман: как тратится"))),
        ("сфера: «в слоте» = «с применением»", raises(
            lambda: _distinct(t, _with(prof, battles=no_carry), "сфера"))),
        ("без ночи в столице «в городе» = «никогда»", raises(lambda: _distinct(
            t, _with(prof, timeline=tuple(("night", "поле") if e == ("night", "столица") else e
                                          for e in tl)), "талисман: восстановление"))),
        ("без второго одинакового посоха", raises(lambda: _distinct(
            t, _with(prof, timeline=tuple(e for e in tl if e != ("cast", "A2"))), "посох"))),
        ("лорд Маг: «на посох» = «без предела»", raises(
            lambda: _distinct(t, _with(prof, lord=LORD_MAGE), "посох"))),
        ("путь дня длиннее запаса хода", raises(
            lambda: check_route(t, _with(prof, route=far_day), "m17"))),
        ("мешок не на пути первого дня", raises(lambda: check_route(
            t, dict(prof, bags=[dict(prof["bags"][0], xy=(3, 40))] + prof["bags"][1:]), "m17"))),
        ("нейтрал иммунен к талисману (Йети)", raises(
            lambda: check_immunity(t, _chain_as(prof, "g000uu0043"), "m17"))),
        ("мишень иммунна к посоху (Чёрный дракон)", raises(lambda: check_immunity(t, dict(
            prof, neutrals=chain(prof) + [dict(dummy(prof), leader="g000uu5022")]), "m17"))),
        ("мишень вне обзора", raises(lambda: check_dummy(t, dict(
            prof, neutrals=chain(prof) + [dict(dummy(prof), xy=(40, 40))]), "m17"))),
        ("таблица гипотез пуста", raises(lambda: check_distinct({}, "пусто", "m17"))),
    ]
