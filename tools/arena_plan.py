#!/usr/bin/env python3
"""Арена Ф6 v2: СОСТАВ и РАСЧЁТ — кто с кем дерётся и сколько это длится в раундах.

    python tools/arena_plan.py              # таблица расчёта, ничего не пишется
    python tools/arena_plan.py --selftest   # проверить сторожей подсадной ошибкой

Карту и маршрут по этим числам собирает `tools/make_arena_map.py`; здесь только данные и
арифметика, поэтому модуль можно гонять сколько угодно — он ничего не записывает.

Почему числа, а не глазомер: первая арена (`c08cdfb`) встала на третьем бою, потому что расчёт
считал врага статичным и не заметил пяти Зомби, призванных Оккультистом за бой. Отсюда три
сторожа, каждый роняет сборку: `check_no_summoners` (призывателей среди врага быть не должно),
`check_cure_setup` (яд ложится раньше хода Матриарха и не гасится иммунитетом) и порог «не
меньше двух ходов носителя» в `print_plan`. Что сторожа правда ловят — доказывает `--selftest`.
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
from dbf_dump import read as dbf_read  # noqa: E402

# Отряды пары ИИ (стоят в стороне от маршрута, см. `make_arena_map.AI_PROBE`) — их юниты тоже
# проходят проверку на призыв, поэтому список живёт здесь, рядом с проверкой.
AI_PROBE_UNITS = ["g000uu5130"]     # по одному солдату сверх лидера, чтобы бой было видно

# Холостых ходов между боями. Регенерация — `Gunits.REGEN` = 5 % максимума за ход; три хода
# дают ~15 % отряда. Больше ходов стоило бы дороже, чем даёт: в боях 1-4 наши архангелы лечат
# быстрее, чем бьёт телохранитель, и отдых нужен на случай, когда расчёт ошибся, а не по плану.
REST_TURNS = 3

# ------------------------------------------------------------------ наш отряд

# Лидер уезжает в ЗАДНИЙ ряд: гибель лидера стоит всего отряда, а у Pegasus Knight 150 HP.
LEADER_CELL = 1
# Порядок важен: `place` раскладывает ближников вперёд (0,2,4), прочих назад (1,3,5).
# Получается POS = [гвардеец, герой, гвардеец, архангел, Master Thug, архангел] — шесть клеток
# из шести, свободных не остаётся.
POISONER = "g000uu5131"   # Master Thug 110 HP, ближний бой; 2-я атака — яд (см. п. 5 шапки)
SQUAD = ["g000uu0044",   # L1 King's Guard  225 HP, 60 урона, ближний бой — передний ряд
         "g000uu0044",   # второй такой же — передний ряд
         POISONER,       # ближник, тоже передний ряд: он и есть источник эффекта для `Cure`
         "g000uu0022",   # L1 Arch-Angel    100 HP, лечение 40, REACH 2 — задний ряд
         "g000uu0022"]   # второй такой же — задний ряд
LORD = "warrior"         # Pegasus Knight (ближний бой), а не Архимаг (площадная атака)
# Тот же герой пишется и в сам сценарий. Старт сцены пересоздаёт игрока по лорду и подменяет
# лидера (`fidelity-f5.md` §5 п. 8), так что решает выбор лорда; но если подмена вдруг не
# случится, в отряде окажется тот же пеший ближник, а не Архимаг с площадной атакой. Обе
# ветки дают один и тот же строй — это и есть смысл дублирования.
HERO_TYPE = "g000uu0019"

# Master Thug взят версией `5131`, а не `5031`: у солдатской `5031` в `Gunits.MOVE` ноль, и
# ставить в отряд юнита с нулевым запасом хода — риск ради ничего. У `5131` MOVE = 25 и та же
# `UNIT_CAT = 2`, что у King's Guard, который в отряде уже отработал живой прогон. Здоровье,
# атака и яд у них совпадают байт в байт.

# ------------------------------------------------------------- телохранители

# Две силы телохранителя. Выбор на бой — расчёт `plan_rounds`, а не вкус: чем дольше живёт
# передний ряд, тем больше ходов успевает сделать носитель.
GUARD_TANK = "g000uu6017"   # Bone Lord     400 HP, 65 урона, DrainOverflow (лечит себя вполовину)
GUARD_MID = "g000uu6006"    # Yataa' Halli  250 HP, броня 20, 80 урона
GUARD_WEAK = "g000uu5130"   # Thug           65 HP, 25 урона — шаблонный лидер нейтрала

SUMMON_CLASS = 17           # Gattacks.CLASS = L_SUMMON: то, что убило первый прогон

# Классы, которые выбивают юнитов из НАШЕГО строя: Баронесса гонит их с поля, Ведьма подменяет
# Бесами. Расчёт этого не моделирует поштучно, но и делать вид, что отряд цел, нельзя — ровно
# такая слепота дала «отряд входит в бой полным» и стоила первого прогона. Консервативное
# допущение, названное вслух: в этих боях лечит ОДИН архангел из двух, а не два.
HURT_OUR_SQUAD = {"Fear", "TransformOther"}

# (класс, носитель, телохранитель-лидер, добавка в отряд, зачем именно так)
BATTLES = [
    ("TransformOther", "g000uu0067", GUARD_MID, [],
     "Ведьма превращает НАШИХ юнитов в Бесов (20 урона): наш урон падает с каждым её ходом, "
     "бой удлиняется сам, и это работает на носителя, а не против него."),
    ("Fear", "g000uu0073", GUARD_MID, [],
     "Баронесса гонит наших юнитов с поля по одному за ход. У нас шесть юнитов, а бой считан "
     "на три-четыре раунда — уйти успеют не все."),
    ("DrainOverflow", GUARD_TANK, GUARD_TANK, [GUARD_WEAK],
     "Носитель и есть телохранитель: Костяной лорд стоит в переднем ряду сам. Разбойник рядом "
     "— получатель излишка вампиризма (в первом раунде лорд полон, и лишнее уходит союзнику)."),
    ("Cure", "g000uu0017", GUARD_MID, [],
     "Матриарх лечит телохранителя первой атакой (40) и снимает эффекты второй. Снимать есть "
     "что: наш Master Thug травит того же телохранителя ядом, и его инициатива 75 против её 10."),
    ("LowerDamage", "g000uu5368", GUARD_WEAK, [],
     "Тиамат сама себе танк (495 HP) и бьёт ВЕСЬ наш строй: обе её атаки REACH 1, вторая и есть "
     "LowerDamage. Бой последний — проиграть его не жалко, четыре класса уже сняты."),
]

# ------------------------------------------------------------------- расчёт

class Numbers:
    """Числа боя из Gunits/Gattacks — чтобы длительность считалась, а не угадывалась."""

    def __init__(self, t: sq.Tables) -> None:
        self.t = t

    def attack(self, unit_id: str, col: str = "ATTACK_ID") -> dict | None:
        aid = str(self.t.unit(unit_id).get(col, "")).lower()
        return self.t.attacks.get(aid)

    def hp(self, unit_id: str) -> int:
        return int(self.t.unit(unit_id)["HIT_POINT"])

    def armor(self, unit_id: str) -> int:
        return int(self.t.unit(unit_id)["ARMOR"] or 0)

    def effective_hp(self, unit_id: str) -> float:
        """Броня режет каждый удар, поэтому в раундах она читается как прибавка к здоровью."""
        return self.hp(unit_id) / (1.0 - min(self.armor(unit_id), 90) / 100.0)

    def dpr(self, unit_id: str, targets: int = 1) -> float:
        """Урон юнита за раунд с поправкой на точность (POWER — вероятность попасть).

        `targets` — сколько целей он накрывает: у площадной атаки (`REACH = 1`, `L_ALL`) это
        весь чужой строй сразу, и против нашего отряда её урон надо умножать. Именно этим
        Тиамат (100 урона по всем) отличается от Костяного лорда (65 по одному).
        """
        a = self.attack(unit_id)
        if not a or int(a["CLASS"]) not in (1, 2, 15):
            return 0.0
        n = targets if int(a["REACH"]) == 1 else 1
        return int(a["QTY_DAM"] or 0) * int(a["POWER"]) / 100.0 * n

    def self_heal(self, unit_id: str) -> float:
        """Вампиризм: половина нанесённого (ранг 1, `sar eax,1` на 0x65be8f и 0x65f953)."""
        a = self.attack(unit_id)
        if not a or int(a["CLASS"]) not in (2, 15):
            return 0.0
        return self.dpr(unit_id) / 2.0

    def regen(self, unit_id: str) -> float:
        """Лечение на карте за ход: `Gunits.REGEN` — проценты максимума (у наших юнитов 5)."""
        r = self.t.unit(unit_id)
        return int(r["HIT_POINT"]) * int(r["REGEN"] or 0) / 100.0

    def heal(self, unit_id: str) -> float:
        """Лечение союзника за раунд по обеим атакам юнита."""
        total = 0.0
        for col in ("ATTACK_ID", "ATTACK2_ID"):
            a = self.attack(unit_id, col)
            if a and int(a["CLASS"]) == 6:
                total += int(a["QTY_HEAL"] or 0) * int(a["POWER"]) / 100.0
        return total


def our_front(n: Numbers) -> list[str]:
    """Кто из нашего отряда реально бьёт: только передний ряд (ближник сзади бить не может)."""
    return [u for u in SQUAD if n.t.is_melee(u)]


def enemy_units() -> list[str]:
    """Все чужие юниты карты: лидеры боёв, носители, довески и пара ИИ — без повторов."""
    out: list[str] = []
    for _cls, carrier, guard, extra, _why in BATTLES:
        out += [guard, carrier] + list(extra)
    out += [GUARD_WEAK] + list(AI_PROBE_UNITS)      # пара ИИ: лидер-шаблон плюс её солдаты
    return sorted(dict.fromkeys(out))


def check_no_summoners(t: sq.Tables, units: list[str] | None = None) -> None:
    """Ни один чужой юнит не смеет уметь призыв — это и убило первый прогон арены.

    Смотрятся ОБЕ колонки атак (`ATTACK_ID` и `ATTACK2_ID`): призыв может стоять второй.
    Печатается всегда — «проверил и не нашёл» тоже результат, и его надо видеть в выводе.

    `units` — список чужих юнитов, если он берётся не из `BATTLES` этого модуля (так его
    передаёт арена предметов `tools/items_plan.py`); без него — состав арены v2, как раньше.
    """
    units = list(enemy_units()) if units is None else list(dict.fromkeys(units))
    bad = []
    print("призыватели среди чужих юнитов (Gattacks.CLASS = %d):" % SUMMON_CLASS)
    for uid in units:
        classes = []
        for col in ("ATTACK_ID", "ATTACK2_ID"):
            a = t.attacks.get(str(t.unit(uid).get(col, "")).lower())
            if a:
                classes.append(int(a["CLASS"]))
        mark = "ПРИЗЫВ" if SUMMON_CLASS in classes else "нет"
        print("  %-12s %-22s классы атак %-10s %s"
              % (uid, t.name(uid), ",".join(str(c) for c in classes), mark))
        if SUMMON_CLASS in classes:
            bad.append("%s (%s)" % (uid, t.name(uid)))
    if bad:
        raise SystemExit("в составе врага призыватели: %s — первый прогон арены встал ровно на "
                         "этом (пять Зомби за бой)" % ", ".join(bad))
    print("  призывателей нет: проверено %d юнитов" % len(units))


POISON_CLASS = 12           # Gattacks.CLASS = L_POISON: то, что Матриарх будет снимать
CURE_CARRIER = "g000uu0017"


def immunities(t: sq.Tables) -> tuple[dict[str, set[int]], dict[str, set[int]]]:
    """Иммунитеты из `Gimmu` (по ИСТОЧНИКУ атаки) и `GimmuC` (по КЛАССУ атаки)."""
    def load(name: str) -> dict[str, set[int]]:
        out: dict[str, set[int]] = {}
        for r in dbf_read(sq.GLOBALS / name)["rows"]:
            out.setdefault(r["UNIT_ID"].lower(), set()).add(int(r["IMMUNITY"]))
        return out
    return load("Gimmu.dbf"), load("GimmuC.dbf")


def check_cure_setup(t: sq.Tables) -> None:
    """`Cure` не должен сработать вхолостую: доказать числом, что яд ляжет и ляжет ВОВРЕМЯ.

    Матриарх снимает негативные эффекты со СВОИХ — значит накладывать их обязаны мы. Проверяются
    три вещи, каждая падением сборки: у нашего отравителя вообще есть яд; яд бьёт раньше хода
    Матриарха (инициатива); её телохранитель к этому яду не иммунен ни по классу, ни по источнику
    (Костяной лорд, например, иммунен к источнику 3 и в носители `Cure` не годится).
    """
    guard = next(g for cls, carrier, g, _e, _w in BATTLES if carrier == CURE_CARRIER)
    poison = next((a for col in ("ATTACK_ID", "ATTACK2_ID")
                   for a in [t.attacks.get(str(t.unit(POISONER).get(col, "")).lower())]
                   if a and int(a["CLASS"]) == POISON_CLASS), None)
    if poison is None:
        raise SystemExit("%s (%s) не накладывает яд (CLASS %d) — `Cure` сработает вхолостую"
                         % (POISONER, t.name(POISONER), POISON_CLASS))
    cure_ini = int(t.attacks[t.unit(CURE_CARRIER)["ATTACK_ID"].lower()]["INITIATIVE"])
    by_source, by_class = immunities(t)
    src, ini = int(poison["SOURCE"]), int(poison["INITIATIVE"])
    print("Cure: яд %s (класс %d, источник %d, инициатива %d, %s%% попадания) по %s"
          % (poison["ATT_ID"], POISON_CLASS, src, ini, poison["POWER"], t.name(guard)))
    print("  инициатива Матриарха %d — яд ложится %s"
          % (cure_ini, "раньше её хода" if ini > cure_ini else "ПОЗЖЕ её хода"))
    if ini <= cure_ini:
        raise SystemExit("инициатива яда %d не выше инициативы Матриарха %d — в первом раунде "
                         "снимать будет нечего" % (ini, cure_ini))
    if POISON_CLASS in by_class.get(guard.lower(), set()):
        raise SystemExit("%s иммунен к классу %d (GimmuC)" % (guard, POISON_CLASS))
    if src in by_source.get(guard.lower(), set()):
        raise SystemExit("%s иммунен к источнику %d (Gimmu) — яд не ляжет" % (guard, src))
    print("  иммунитета у телохранителя нет: Gimmu %s, GimmuC %s"
          % (sorted(by_source.get(guard.lower(), set())) or "-",
             sorted(by_class.get(guard.lower(), set())) or "-"))


def plan_rounds(t: sq.Tables) -> list[dict]:
    """Сколько раундов проживёт каждый бой и сколько ходов успеет сделать носитель.

    Считается две границы, и берётся меньшая:

    * **раунды до заднего ряда** — пока жив передний ряд врага, носитель недосягаем и ходит
      каждый раунд: `HP переднего ряда / (наш урон − их лечение)`;
    * **раунды до гибели нашего отряда** — `наши HP / (их урон − наше лечение)`, где «наши HP»
      это то, с чем отряд ВОШЁЛ в бой, а не максимум: потери переносятся из боя в бой, между
      боями прибавляется регенерация холостых ходов. Площадная атака (`REACH = 1`) умножается
      на число наших юнитов — у Тиамат 100 урона превращаются в 600 на строй из шести.
    """
    n = Numbers(t)
    ours = our_front(n)
    us = len(SQUAD) + 1                       # пятеро солдат и герой
    our_dpr = sum(n.dpr(u) for u in ours)
    our_heal = sum(n.heal(u) for u in SQUAD)
    hp_max = sum(n.hp(u) for u in SQUAD) + n.hp(HERO_TYPE)
    regen = sum(n.regen(u) for u in SQUAD) + n.regen(HERO_TYPE)
    hp = float(hp_max)
    rows = []
    for k, (cls, carrier, guard, extra, _why) in enumerate(BATTLES):
        front = [guard] + [u for u in extra if n.t.is_melee(u)]
        back = [u for u in extra if not n.t.is_melee(u)]
        if carrier == guard:
            pass                              # носитель и телохранитель — один юнит, уже в строю
        elif t.is_big(carrier) or n.t.is_melee(carrier):
            front = front + [carrier]         # крупный или ближний носитель встаёт в передний ряд
        else:
            back = back + [carrier]
        front_hp = sum(n.effective_hp(u) for u in front)
        heal_them = sum(n.self_heal(u) for u in front) + sum(n.heal(u) for u in front + back)
        net = max(our_dpr - heal_them, 1.0)
        rounds = math.ceil(front_hp / net)
        their_dpr = sum(n.dpr(u, targets=us) for u in front + back)
        heal_us = our_heal / 2.0 if cls in HURT_OUR_SQUAD else our_heal
        drain = their_dpr - heal_us
        hp_in = hp
        survive = math.ceil(hp_in / drain) if drain > 0 else rounds
        turns = min(rounds, survive)
        hp = max(hp_in - max(drain, 0.0) * turns, 0.0)
        rest = REST_TURNS if k + 1 < len(BATTLES) else 0
        hp_rested = min(float(hp_max), hp + regen * rest)
        rows.append({"класс": cls, "носитель": carrier, "телохранитель": guard,
                     "передний ряд": [n.t.name(u) for u in front],
                     "HP переднего ряда": round(front_hp),
                     "их лечение/раунд": round(heal_them),
                     "наш урон/раунд": round(our_dpr),
                     "раундов до заднего ряда": rounds,
                     "HP отряда на входе": round(hp_in),
                     "HP отряда на выходе": round(hp),
                     "холостых ходов после": rest,
                     "HP после отдыха": round(hp_rested),
                     "раундов живём мы": survive,
                     "ходов носителя": turns,
                     "их урон/раунд": round(their_dpr),
                     "наше лечение/раунд": round(heal_us),
                     "нам в минус/раунд": round(drain)})
        hp = hp_rested
    return rows


def print_plan(t: sq.Tables) -> list[dict]:
    n = Numbers(t)
    ours = our_front(n)
    hp_max = sum(n.hp(u) for u in SQUAD) + n.hp(HERO_TYPE)
    regen = sum(n.regen(u) for u in SQUAD) + n.regen(HERO_TYPE)
    check_no_summoners(t)
    print()
    print("наш отряд: герой Pegasus Knight (задний ряд, клетка %d) + %s"
          % (LEADER_CELL, ", ".join(n.t.name(u) for u in SQUAD)))
    print("  бьют только %s: суммарно %d урона в раунд с учётом точности"
          % (", ".join(n.t.name(u) for u in ours), round(sum(n.dpr(u) for u in ours))))
    print("  лечат %d в раунд; всего HP %d, регенерация на карте %d за холостой ход (%d ходов "
          "между боями)" % (round(sum(n.heal(u) for u in SQUAD)), hp_max, round(regen), REST_TURNS))
    check_cure_setup(t)
    print()
    rows = plan_rounds(t)
    print("%-15s %-16s %6s %6s %6s %6s %7s %6s %6s %6s  %s"
          % ("класс", "телохранитель", "HP ряд", "нетто", "раунд", "живём", "нам -/р",
             "HP вход", "HP вых", "ходов", "носитель"))
    for r in rows:
        print("%-15s %-16s %6d %6d %6d %6d %7d %6d %6d %6d  %s"
              % (r["класс"], t.name(r["телохранитель"]), r["HP переднего ряда"],
                 max(r["наш урон/раунд"] - r["их лечение/раунд"], 1), r["раундов до заднего ряда"],
                 r["раундов живём мы"], r["нам в минус/раунд"], r["HP отряда на входе"],
                 r["HP отряда на выходе"], r["ходов носителя"], t.name(r["носитель"])))
    worst = min(r["ходов носителя"] for r in rows)
    print("минимум ходов носителя по всем боям: %d (критерий Ф6 — не меньше 2)" % worst)
    if worst < 2:
        raise SystemExit("расчёт не держит критерий: носитель успевает сходить меньше двух раз")
    return rows



def _raises(fn) -> str | None:
    """Текст SystemExit, если сторож сработал; None — если пропустил. Печать сторожа глушится."""
    try:
        with contextlib.redirect_stdout(io.StringIO()):
            fn()
    except SystemExit as e:
        return str(e)
    return None


def selftest() -> int:
    """Сторожа проверяются подсадной ошибкой: «упало бы» надо доказать, а не пообещать.

    Ничего не пишет и не запускает игру. Три случая — ровно те три, на которых встал или мог бы
    встать прогон: призыватель в составе врага, иммунный к яду телохранитель `Cure` и отряд без
    отравителя вовсе.
    """
    global BATTLES, POISONER
    t = sq.Tables()
    saved_b, saved_p = BATTLES, POISONER
    cases: list[tuple[str, str | None]] = []
    try:
        BATTLES = list(saved_b) + [("Summon", "g000uu6013", GUARD_MID, [], "подсадной призыватель")]
        cases.append(("призыватель в составе врага", _raises(lambda: check_no_summoners(t))))
        BATTLES = [b if b[1] != CURE_CARRIER else (b[0], b[1], GUARD_TANK, b[3], b[4])
                   for b in saved_b]
        cases.append(("телохранитель Cure иммунен к яду", _raises(lambda: check_cure_setup(t))))
        BATTLES = saved_b
        POISONER = "g000uu0044"          # King's Guard: яда у него нет
        cases.append(("в отряде нет отравителя", _raises(lambda: check_cure_setup(t))))
    finally:
        BATTLES, POISONER = saved_b, saved_p
    bad = 0
    for name, msg in cases:
        print("%-36s %s" % (name, ("ловится: " + msg) if msg else "ПРОПУЩЕН"))
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
