#!/usr/bin/env python3
"""Арена ПРЕДМЕТОВ Ф6: состав, предметы и расчёт трёх боёв, которые играет пользователь.

    python tools/items_plan.py              # таблица расчёта, ничего не пишется
    python tools/items_plan.py --selftest   # проверить сторожей подсадной ошибкой

Карту и маршрут по этим числам собирает `tools/make_items_map.py`; здесь только данные и
арифметика, поэтому модуль можно гонять сколько угодно — он ничего не записывает.

**Зачем эта арена.** Модель предметов (`src/D2.Rules/Magic/Item.cs`, `ItemUse.cs`) построена
целиком по дизассемблеру, а в корпусе Ф6 **ноль применений предмета**. Автобоем это не снять:
ИИ зелья не пьёт, а у применения предмета хоткея нет вовсе (`docs/game-hotkeys.md`). Значит бои
играет человек, а драйвер только доводит отряд, печатает инструкцию и ждёт `battle_end`.

**Чем меряем.** События «применён предмет» в схеме v4 **нет**, зонд его не пишет. Поэтому
предмет опознаётся по следу, и след выбран так, чтобы он был однозначным:

* **зелье силы** `g000um2015` = +30 % `L_QTY_DAMAGE` -> `dmg_buffs.ret` того же юнита прыгает
  с 60 на 78 (`GmodifL`, проверено чтением таблицы);
* **зелье точности** `g000um2009` = +30 % `L_POWER` -> `power.ret` прыгает с 80 на 104
  (если движок режет по 100 — увидим 100, и это тоже ответ, а не молчание);
* **лечебные зелья** -> рост `hp` юнита между снимками `units[]` на 50 и на 200. В отряде
  **нет ни одного лекаря**, а всем вражеским атакам разрешён только чистый урон
  (`check_plain_melee`) — иначе рост HP ничего не доказывал бы;
* **сфера и талисман** -> `attack_new` с `disp_class` 23 (`Blister`) и 13 (`Frostbite`). Эти
  два класса на карте не умеет **никто** (сторож `check_item_classes`), поэтому одна такая
  строка в журнале — прямое доказательство, а не косвенный признак. Заодно оба класса Ф6
  живьём ещё не снимала;
* **уничтожение после боя** -> разбор сохранения сразу после боя: сферы в отряде больше нет,
  оба талисмана на месте.

Воскрешение измерить наверняка нельзя: нужен павший союзник, а кого убьёт враг — решает не
маршрут. Поэтому шаг воскрешения печатает состояние отряда и предметов, но прогон на нём не
падает; так и сказано в отчёте.
"""
from __future__ import annotations

import argparse
import contextlib
import io
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import scenario_squads as sq  # noqa: E402
import arena_plan as ap  # noqa: E402  (Numbers и сторож призыва — общие с ареной v2)
from dbf_dump import read as dbf_read  # noqa: E402

FRONT_CELLS = 3          # клетки 0,2,4: только они бьют, пока передний ряд жив
HP_FLOOR = 0.20          # ниже этой доли максимума отряд входить в следующий бой не должен

# ------------------------------------------------------------------ наш отряд

# Лидер в ЗАДНЕМ ряду (клетка 1) — он и есть тот, кто применяет предметы. Если бы он стоял
# впереди, ближний бой врага мог бы его убить, и вся арена кончилась бы на первом бою.
LEADER_CELL = 1
LORD = "warrior"            # Pegasus Knight (REACH 3), а не Архимаг (площадная атака)
HERO_TYPE = "g000uu0019"
WEAK = "g000uu5130"         # Thug 65 HP — заведомый покойник для зелья воскрешения
SQUAD = ["g000uu0044",      # King's Guard 225 HP, 60 урона — он и пьёт оба бустовых зелья
         "g000uu0044",      # второй такой же
         WEAK]
# Лекарей в отряде НЕТ намеренно: рост HP должен означать зелье и только зелье.

# ------------------------------------------------------------------- предметы

ITEM_HEAL_SMALL = "G000IG0005"   # Potion of Healing,  ITEM_CAT 5, HP_POTION 50
ITEM_HEAL_BIG = "G000IG0018"     # Healing Ointment,   ITEM_CAT 5, HP_POTION 200
ITEM_STRENGTH = "G000IG0015"     # Potion of Strength, ITEM_CAT 4, MOD_POTION g000um2015
ITEM_ACCURACY = "G000IG0009"     # Potion of Accuracy, ITEM_CAT 4, MOD_POTION g000um2009
ITEM_ORB = "G000IG9041"          # Orb of Flames,      ITEM_CAT 11, ATTACK_ID g000aa9141 (Blister)
ITEM_TALISMAN = "G000IG9137"     # Talisman of Frost,  ITEM_CAT 12, ATTACK_ID g000aa9137 (Frostbite)
ITEM_REVIVE = "G000IG0001"       # Life Potion,        ITEM_CAT 6

# Что кладём в инвентарь лидера. Талисман — ДВА одинаковых: дубль проверяет ветку «предмет
# ищется в инвентаре», в которой одноимённая вещь не должна утянуть за собой вторую.
INVENTORY = [ITEM_STRENGTH, ITEM_ACCURACY, ITEM_HEAL_SMALL, ITEM_HEAL_BIG,
             ITEM_ORB, ITEM_TALISMAN, ITEM_TALISMAN, ITEM_REVIVE]
TALISMAN_CHARGES = 5             # как в ванильных картах (MidTalismanCharges, 'Greenskins'.sg)

ITEM_WHY = {
    ITEM_HEAL_SMALL: "лечение 50: рост hp юнита на 50 между снимками units[]",
    ITEM_HEAL_BIG: "лечение 200: рост hp на 200 (или до максимума)",
    ITEM_STRENGTH: "модификатор урона: dmg_buffs.ret 60 -> 78 у того же King's Guard",
    ITEM_ACCURACY: "модификатор точности: power.ret 80 -> 104 у того же King's Guard",
    ITEM_ORB: "предмет-атака, класс Blister (23); после боя обязан ИСЧЕЗНУТЬ из отряда",
    ITEM_TALISMAN: "предмет-атака, класс Frostbite (13); оба обязаны ОСТАТЬСЯ (5 зарядов)",
    ITEM_REVIVE: "воскрешение павшего на карте; измеряется состоянием отряда, не журналом",
}

# Классы атак предметов — их не смеет уметь ни один юнит карты, иначе строка attack_new
# перестаёт быть доказательством. Номера — Gattacks.CLASS / Globals/LattC.dbf.
ITEM_CLASSES = {23: "Blister", 13: "Frostbite"}

# Что вражеским атакам разрешено: только чистый урон. Почему — в check_plain_melee.
ENEMY_CLASSES = {1}

# --------------------------------------------------------------------- бои

# Гном — самый «долгий» ближник ванили: 150 HP при 30 уроне, отношение 5,0 против 3,75 у
# King's Guard. Долгий бой при слабом уроне — ровно то, что нужно, когда за бой надо успеть
# применить два предмета и ещё увидеть базу до применения.
DWARF_LEAD = "g000uu5236"   # Dwarf, MOVE 20 — годится в лидеры отряда
DWARF = "g000uu0036"        # Dwarf, MOVE 0 — только в солдаты

REST_TURNS = 3              # холостых ходов там, где отдых нужен (Gunits.REGEN = 5 % за ход)

BATTLES = [
    {"name": "boosts", "leader": DWARF_LEAD, "soldiers": [DWARF, DWARF, DWARF],
     "slots": [ITEM_STRENGTH, ITEM_ACCURACY], "min_rounds": 4, "heal": 0, "rest_after": 0,
     "why": "Четвёртый гном стоит в ЗАДНЕМ ряду: ближний бой оттуда не бьёт, поэтому он "
            "удлиняет бой, НЕ добавляя нам урона. Четыре раунда нужны, чтобы в первом снять "
            "базу того же King's Guard, во втором дать ему зелье силы, в третьем — точности.",
     "steps": ["раунд 1 — НИЧЕГО не применять: пусть King's Guard просто ударит (это база, "
               "без неё сравнивать не с чем);",
               "раунд 2 — лидером применить Potion of Strength на ТОГО ЖЕ King's Guard;",
               "раунд 3 — лидером применить Potion of Accuracy на него же;",
               "раунд 4 и дальше — обычные атаки, добить гномов."]},
    {"name": "heal", "leader": DWARF_LEAD, "soldiers": [DWARF],
     "slots": [ITEM_HEAL_SMALL, ITEM_HEAL_BIG], "min_rounds": 3, "heal": 250, "rest_after": REST_TURNS,
     "why": "Отдыха перед этим боем НЕТ намеренно: лечебное зелье движок отказывается давать "
            "юниту с полным здоровьем (PotionResult.AlreadyAtFullHp, 0x5de639), а регенерация "
            "на карте только приблизила бы отряд к полному.",
     "steps": ["раунд 1 — Potion of Healing (50) на самого раненого нашего юнита;",
               "раунд 2 — Healing Ointment (200) на другого раненого (не на того же, иначе "
               "второе лечение упрётся в максимум и прибавка будет меньше 200);",
               "дальше — обычные атаки."]},
    {"name": "attack_items", "leader": DWARF_LEAD, "soldiers": [DWARF],
     "slots": [ITEM_ORB, ITEM_TALISMAN], "min_rounds": 3, "heal": 0, "rest_after": 0,
     "why": "Сфера одноразова, у талисмана 5 зарядов — разницу проверяет разбор сохранения "
            "сразу после боя, а сам факт применения — класс атаки в журнале.",
     "steps": ["раунд 1 — применить сферу Orb of Flames по любому гному;",
               "раунд 2 — применить талисман Talisman of Frost по любому гному;",
               "дальше — обычные атаки, бой надо ВЫИГРАТЬ (иначе предметы проверять не по чему)."]},
]


# ------------------------------------------------------------------- сторожа

def enemy_units() -> list[str]:
    out: list[str] = []
    for b in BATTLES:
        out += [b["leader"]] + list(b["soldiers"])
    return list(dict.fromkeys(out))


def all_units() -> list[str]:
    return list(dict.fromkeys(enemy_units() + SQUAD + [HERO_TYPE]))


def unit_classes(t: sq.Tables, uid: str) -> list[int]:
    out = []
    for col in ("ATTACK_ID", "ATTACK2_ID"):
        a = t.attacks.get(str(t.unit(uid).get(col, "")).lower())
        if a:
            out.append(int(a["CLASS"]))
    return out


def check_plain_melee(t: sq.Tables) -> None:
    """Каждая вражеская атака обязана быть ближней и ЧИСТЫМ уроном (`REACH 3`, `CLASS 1`).

    Две причины, и обе числовые.

    * **Дальнобойность.** Лидер стоит в заднем ряду и он ЕДИНСТВЕННЫЙ, кто применяет предметы:
      его смерть кончает арену на месте. Ближний бой бьёт только передний ряд (подтверждено
      прогоном арены v2), а `REACH 1` (все) и `REACH 2` (любая цель) достают заднего.
    * **Класс.** Любой класс, кроме чистого урона, портит ИЗМЕРЕНИЕ. `LowerDamage` роняет
      `dmg_buffs.ret` нашего юнита (в журнале арены v2 King's Guard 60 -> 41, и счётчик роста
      принял бы возврат к 60 за зелье силы); вампиризм и лечение поднимают `hp` и подделали бы
      лечебное зелье. У гнома одна атака класса 1 и больше ничего — на этом всё измерение и
      стоит.
    """
    bad = []
    print("вражеские атаки (нужен REACH L_ADJACENT и CLASS %s):" % sorted(ENEMY_CLASSES))
    for uid in enemy_units():
        pairs = []
        for col in ("ATTACK_ID", "ATTACK2_ID"):
            a = t.attacks.get(str(t.unit(uid).get(col, "")).lower())
            if a:
                pairs.append((int(a["REACH"]), int(a["CLASS"])))
        ok = bool(pairs) and all(r == t.melee and c in ENEMY_CLASSES for r, c in pairs)
        print("  %-12s %-22s (REACH,CLASS) %-14s %s"
              % (uid, t.name(uid), ";".join("%d,%d" % p for p in pairs),
                 "годится" if ok else "НЕ ГОДИТСЯ"))
        if not ok:
            bad.append("%s (%s)" % (uid, t.name(uid)))
    if bad:
        raise SystemExit("у врага атаки не «ближний чистый урон»: %s — либо достанут лидера в "
                         "заднем ряду, либо испортят измерение роста урона и HP" % ", ".join(bad))
    print("  все атаки ближние и чистый урон: проверено %d юнитов" % len(enemy_units()))


def check_item_classes(t: sq.Tables) -> None:
    """Класс атаки предмета не смеет уметь ни один юнит карты — иначе он не доказательство."""
    items = {r["ITEM_ID"]: r for r in dbf_read(sq.GLOBALS / "GItem.DBF")["rows"]}
    print("классы атак предметов (доказательство применения по attack_new.disp_class):")
    want = {}
    for iid in (ITEM_ORB, ITEM_TALISMAN):
        row = items.get(iid)
        if row is None:
            raise SystemExit("предмета %s нет в GItem.DBF" % iid)
        att = t.attacks.get(str(row["ATTACK_ID"]).lower())
        if att is None:
            raise SystemExit("у предмета %s пустой ATTACK_ID — он не предмет-атака" % iid)
        cid = int(att["CLASS"])
        if cid not in ITEM_CLASSES:
            raise SystemExit("предмет %s даёт класс %d, а арена рассчитана на %s"
                             % (iid, cid, sorted(ITEM_CLASSES)))
        want[cid] = iid
        print("  %-12s %-24s атака %s класс %d (%s)"
              % (iid, t.text.get(row["NAME_TXT"].lower(), iid), att["ATT_ID"], cid,
                 ITEM_CLASSES[cid]))
    if set(want) != set(ITEM_CLASSES):
        raise SystemExit("классы предметов %s не покрывают %s" % (sorted(want), sorted(ITEM_CLASSES)))
    clash = [(uid, c) for uid in all_units() for c in unit_classes(t, uid) if c in want]
    if clash:
        raise SystemExit("класс атаки предмета умеет и юнит карты: %s — строка attack_new "
                         "перестаёт быть доказательством применения предмета"
                         % ", ".join("%s класс %d" % (u, c) for u, c in clash))
    print("  ни один из %d юнитов карты этих классов не умеет" % len(all_units()))


# ------------------------------------------------------------------- расчёт

def plan_rounds(t: sq.Tables) -> list[dict]:
    """Сколько раундов длится каждый бой и с каким здоровьем отряд из него выходит.

    Модель нарочно грубая и КОНСЕРВАТИВНАЯ в опасную сторону: лечение зельями засчитывается
    после боя (то есть во время боя отряд считается нелеченым), а наши потери не уменьшают
    наш урон. Первая арена встала ровно на противоположной ошибке — «враг статичен».

    Передний ряд врага (`FRONT_CELLS` клеток) бьёт и получает; задний вступает, лишь когда
    передний вымер. Отсюда две фазы, и четвёртый гном первого боя удлиняет бой бесплатно.
    """
    n = ap.Numbers(t)
    ours = [u for u in SQUAD if t.is_melee(u)]     # герой в заднем ряду не бьёт
    our_dpr = sum(n.dpr(u) for u in ours)
    if our_dpr <= 0:
        raise SystemExit("наш отряд не наносит урона — бой не кончится")
    hp_max = sum(n.hp(u) for u in SQUAD) + n.hp(HERO_TYPE)
    regen = sum(n.regen(u) for u in SQUAD) + n.regen(HERO_TYPE)
    hp = float(hp_max)
    rows = []
    for b in BATTLES:
        units = [b["leader"]] + list(b["soldiers"])
        front, back = units[:FRONT_CELLS], units[FRONT_CELLS:]
        r1 = math.ceil(sum(n.effective_hp(u) for u in front) / our_dpr)
        r2 = math.ceil(sum(n.effective_hp(u) for u in back) / our_dpr) if back else 0
        loss = sum(n.dpr(u) for u in front) * r1 + sum(n.dpr(u) for u in back) * r2
        hp_in = hp
        hp_out = max(hp_in - loss, 0.0)
        hp_healed = min(float(hp_max), hp_out + b["heal"])
        rest = int(b["rest_after"])
        hp = min(float(hp_max), hp_healed + regen * rest)
        rows.append({"бой": b["name"], "врагов": len(units), "передний ряд": len(front),
                     "HP врага": round(sum(n.effective_hp(u) for u in units)),
                     "наш урон/раунд": round(our_dpr), "их урон/раунд": round(sum(n.dpr(u) for u in front)),
                     "раундов": r1 + r2, "нужно раундов": int(b["min_rounds"]),
                     "HP отряда на входе": round(hp_in), "HP отряда на выходе": round(hp_out),
                     "влито зельями": b["heal"], "холостых ходов после": rest,
                     "HP после отдыха": round(hp), "предметы": b["slots"]})
    return rows


def check_plan(rows: list[dict], hp_max: int) -> None:
    """Расчёт обязан держать два условия, и оба названы числом."""
    short = [r for r in rows if r["раундов"] < r["нужно раундов"]]
    if short:
        raise SystemExit("бой %s длится %d раундов при нужных %d — пользователь не успеет "
                         "применить предметы" % (short[0]["бой"], short[0]["раундов"],
                                                 short[0]["нужно раундов"]))
    floor = HP_FLOOR * hp_max
    low = [r for r in rows if r["HP после отдыха"] < floor]
    if low:
        raise SystemExit("после боя %s у отряда %d HP при пороге %d (%d %% максимума) — "
                         "следующий бой он не переживёт"
                         % (low[0]["бой"], low[0]["HP после отдыха"], round(floor),
                            round(HP_FLOOR * 100)))


def print_plan(t: sq.Tables) -> list[dict]:
    n = ap.Numbers(t)
    ap.check_no_summoners(t, enemy_units())
    print()
    check_plain_melee(t)
    print()
    check_item_classes(t)
    print()
    hp_max = sum(n.hp(u) for u in SQUAD) + n.hp(HERO_TYPE)
    print("наш отряд: лидер %s (задний ряд, клетка %d, применяет предметы) + %s"
          % (t.name(HERO_TYPE), LEADER_CELL, ", ".join(t.name(u) for u in SQUAD)))
    print("  бьют только %s: %d урона в раунд с учётом точности; всего HP %d, лекарей нет"
          % (", ".join(t.name(u) for u in SQUAD if t.is_melee(u)),
             round(sum(n.dpr(u) for u in SQUAD if t.is_melee(u))), hp_max))
    print()
    print("предметы в инвентаре лидера (%d штук, %d видов):" % (len(INVENTORY), len(set(INVENTORY))))
    items = {r["ITEM_ID"]: r for r in dbf_read(sq.GLOBALS / "GItem.DBF")["rows"]}
    for iid in dict.fromkeys(INVENTORY):
        row = items[iid]
        print("  %-12s cat %-2d x%d  %-24s %s"
              % (iid, row["ITEM_CAT"], INVENTORY.count(iid),
                 t.text.get(row["NAME_TXT"].lower(), iid), ITEM_WHY[iid]))
    print()
    rows = plan_rounds(t)
    print("%-14s %6s %8s %8s %8s %8s %8s %8s  %s"
          % ("бой", "врагов", "HP врага", "раундов", "нужно", "HP вход", "HP выход", "HP отдых",
             "предметы"))
    for r in rows:
        print("%-14s %6d %8d %8d %8d %8d %8d %8d  %s"
              % (r["бой"], r["врагов"], r["HP врага"], r["раундов"], r["нужно раундов"],
                 r["HP отряда на входе"], r["HP отряда на выходе"], r["HP после отдыха"],
                 "+".join(r["предметы"])))
    check_plan(rows, hp_max)
    print("расчёт держит оба условия: раундов хватает, отряд не опускается ниже %d %% максимума"
          % round(HP_FLOOR * 100))
    return rows


# ------------------------------------------------------------------ selftest

def _raises(fn) -> str | None:
    try:
        with contextlib.redirect_stdout(io.StringIO()):
            fn()
    except SystemExit as e:
        return str(e)
    return None


def selftest() -> int:
    """Сторожа проверяются подсадной ошибкой: «упало бы» надо доказать, а не пообещать."""
    global BATTLES, ITEM_ORB
    t = sq.Tables()
    saved_b, saved_orb = BATTLES, ITEM_ORB
    hp_max = sum(ap.Numbers(t).hp(u) for u in SQUAD) + ap.Numbers(t).hp(HERO_TYPE)
    cases: list[tuple[str, str | None]] = []
    try:
        BATTLES = list(saved_b) + [{"name": "x", "leader": "g000uu6013", "soldiers": [],
                                    "slots": [], "min_rounds": 1, "heal": 0, "rest_after": 0,
                                    "why": "", "steps": []}]
        cases.append(("призыватель в составе врага",
                      _raises(lambda: ap.check_no_summoners(t, enemy_units()))))
        BATTLES = list(saved_b) + [{"name": "x", "leader": "g000uu0006", "soldiers": [],
                                    "slots": [], "min_rounds": 1, "heal": 0, "rest_after": 0,
                                    "why": "", "steps": []}]   # Archer, REACH 2 — достанет лидера
        cases.append(("вражеская атака не ближняя", _raises(lambda: check_plain_melee(t))))
        # Костяной лорд бьёт вампиризмом (класс 15): он поднимал бы себе hp и подделал зелье.
        BATTLES = list(saved_b) + [{"name": "x", "leader": "g000uu6017", "soldiers": [],
                                    "slots": [], "min_rounds": 1, "heal": 0, "rest_after": 0,
                                    "why": "", "steps": []}]
        cases.append(("вражеская атака не чистый урон", _raises(lambda: check_plain_melee(t))))
        # Son of Ymir умеет Frostbite (13) второй атакой — тот же класс, что у талисмана.
        BATTLES = list(saved_b) + [{"name": "x", "leader": "g000uu0159", "soldiers": [],
                                    "slots": [], "min_rounds": 1, "heal": 0, "rest_after": 0,
                                    "why": "", "steps": []}]
        cases.append(("класс атаки предмета умеет юнит карты",
                      _raises(lambda: check_item_classes(t))))
        BATTLES = saved_b
        ITEM_ORB = "G000IG9022"      # Orb of Fire: класс Damage, арена на него не рассчитана
        cases.append(("предмет не того класса", _raises(lambda: check_item_classes(t))))
        ITEM_ORB = saved_orb
        BATTLES = [dict(b, soldiers=[]) if b["name"] == "boosts" else b for b in saved_b]
        cases.append(("бой короче, чем нужно предметам",
                      _raises(lambda: check_plan(plan_rounds(t), hp_max))))
        BATTLES = [dict(b, soldiers=[DWARF] * 5) for b in saved_b]
        cases.append(("отряд не переживает арену",
                      _raises(lambda: check_plan(plan_rounds(t), hp_max))))
    finally:
        BATTLES, ITEM_ORB = saved_b, saved_orb
    bad = 0
    for name, msg in cases:
        print("%-40s %s" % (name, ("ловится: " + msg) if msg else "ПРОПУЩЕН"))
        bad += msg is None
    print("selftest: %d из %d сторожей сработали" % (len(cases) - bad, len(cases)))
    return 1 if bad else 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--selftest", action="store_true", help="проверить сторожей подсадной ошибкой")
    a = p.parse_args(argv)
    if a.selftest:
        return selftest()
    print_plan(sq.Tables())
    return 0


if __name__ == "__main__":
    sys.exit(main())
