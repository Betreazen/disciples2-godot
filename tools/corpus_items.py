#!/usr/bin/env python3
"""Арена предметов: третий вид действия прогона, сверки по сохранению и счётчики по журналу.

Отдельный модуль, а не правка `corpus_f6_run.py`, по двум причинам: тот и без того у потолка
в 800 строк, и всё здешнее нужно ровно одному маршруту (`tools/items_route.json`).

**Событий «применён предмет» в схеме v4 нет** — зонд их не пишет. Поэтому применение
опознаётся по следу, и каждый след здесь назван поимённо:

| предмет | след |
|---|---|
| зелье силы (`MOD_POTION g000um2015`, +30 % `L_QTY_DAMAGE`) | `dmg_buffs.ret` со строк `random: false` вырос в 1,25 раза против собственного минимума |
| зелье точности (`g000um2009`, +30 % `L_POWER`) | `power.ret` вырос в 1,25 раза против собственного минимума |
| лечебные зелья (`HP_POTION` 50 и 200) | `hp` юнита вырос за один снимок `units[]` на 50 и больше; лекарей на карте нет |
| сфера и талисман (`ATTACK_ID`) | `attack_new` с классом, которого не умеет ни один юнит карты |
| уничтожение после боя | разбор сохранения: сферы в отряде нет, оба талисмана на месте |

Воскрешение прямого следа не имеет: шаг печатает состав отряда с HP и инвентарь, а вывод
делает человек. Врать счётчиком «воскрешений 1» на основании того, что предмет пропал, нельзя.
"""
from __future__ import annotations

import json
import struct
import sys
import time
from collections import Counter, defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sgfile as sg  # noqa: E402
import make_f6_map as f6  # noqa: E402
import items_plan as ip  # noqa: E402

SLOTS = ("BANNER", "TOME", "BATTLE1", "BATTLE2", "ARTIFACT1", "ARTIFACT2", "BOOTS")
BATTLE_SLOTS = ("BATTLE1", "BATTLE2")
WHAT_ITEM = ".?AVCMidItem@@"
SNAPSHOT_EVENTS = ("battle_start", "round", "turn_before", "turn_after", "battle_end")


# ------------------------------------------------------- чтение сохранения

def leader_stack(scen: sg.Sg) -> dict:
    player = f6.human_player(scen)["PLAYER_ID"]
    for r in scen.find(sg.WHAT_STACK):
        st = sg.parse_stack(r.body)
        if st["OWNER"] == player:
            return st
    raise RuntimeError("в сохранении нет отряда игрока")


def unit_hp(body: bytes) -> int:
    """HP юнита из тела `MidUnit` (раскладка — `tools/ksy/sg.ksy`, `mid_unit`)."""
    c = sg.Cursor(body)
    c.fields("UNIT_ID:s TYPE:s LEVEL:i")
    for _ in range(c.count()):
        c.str("MODIF_ID")
    c.fields("CREATION:i NAME_TXT:s")
    if c.bool("TRANSF"):
        c.fields("ORIGTYPEID:s KEEP_HP:b ORIG_XP:i HP_BEFORE:i HP_BEF_MAX:i")
    c.bool("DYNLEVEL")
    return c.int("HP")


def squad_hp(scen: sg.Sg, st: dict) -> list[tuple[str, int]]:
    """(тип юнита, текущие HP) по отряду лидера. Ноль HP — павший: его и воскрешают."""
    bodies = {r.obj_id.upper(): r.body for r in scen.find(sg.WHAT_UNIT)}
    out = []
    for k in range(6):
        uid = st[f"UNIT_{k}"]
        if uid == sg.NULL_ID:
            continue
        body = bodies[uid.upper()]
        out.append((str(sg.fields(body, sg.LAYOUT_UNIT)["TYPE"]).lower(), unit_hp(body)))
    return out


def held(scen: sg.Sg, st: dict) -> tuple[Counter, dict[str, str]]:
    """Что у отряда на руках: счётчик типов вещей и содержимое надетых слотов."""
    kinds = {r.obj_id.upper(): sg.fields(r.body, "ITEM_ID:s ITEM_TYPE:s")["ITEM_TYPE"]
             for r in scen.find(WHAT_ITEM)}
    counts = Counter(kinds.get(i.upper(), i) for i in st["ITEMS"])
    slots = {}
    for name in SLOTS:
        if st[name] != sg.NULL_ID:
            slots[name] = kinds.get(st[name].upper(), st[name])
            counts[slots[name]] += 1
    return counts, slots


def check_battle_slots(scen: sg.Sg, value: str) -> str:
    """Разложил ли пользователь нужную пару по боевым слотам.

    Падаем только если вещи нет НИГДЕ — ни в слотах, ни в инвентаре: как движок хранит
    надетую вещь, ванильные карты не показывают (во всех 34 картах `Exports-archive` слоты
    пусты), и жёсткая проверка была бы догадкой, которая уронит прогон, идущий вживую.
    Куда вещь на самом деле легла — печатается, и это первый честный ответ на вопрос.
    """
    st = leader_stack(scen)
    want = [v for v in value.split(",") if v]
    counts, slots = held(scen, st)
    missing = [w for w in want if not counts[w]]
    if missing:
        raise RuntimeError("вещей %s нет у отряда ни в слотах, ни в инвентаре (есть %s)"
                           % (missing, dict(counts)))
    in_battle = {slots[s] for s in BATTLE_SLOTS if s in slots}
    note = "боевые слоты: %s; всего на руках: %s" % (
        {s: slots[s] for s in BATTLE_SLOTS if s in slots} or "пусты", dict(counts))
    if not set(want) <= in_battle:
        note += ("  ВНИМАНИЕ: в BATTLE1/BATTLE2 не видно %s — либо не разложено, либо движок "
                 "держит надетое иначе" % [w for w in want if w not in in_battle])
    return note


def check_inventory(scen: sg.Sg, value: str) -> str:
    """Предметы отряда после боя. `?` — только отчёт; иначе `-ТИП` (нет) и `ТИП*N` (ровно N)."""
    st = leader_stack(scen)
    counts, slots = held(scen, st)
    note = "на руках: %s; слоты: %s; отряд: %s" % (
        dict(counts), slots or "пусты",
        ", ".join("%s %d HP" % row for row in squad_hp(scen, st)))
    if value.strip() == "?":
        return note
    bad = []
    for token in (v for v in value.split(",") if v):
        if token.startswith("-"):
            if counts[token[1:]]:
                bad.append("%s остался (%d шт.)" % (token[1:], counts[token[1:]]))
        elif "*" in token:
            kind, n = token.split("*")
            if counts[kind] != int(n):
                bad.append("%s: %d шт., ожидалось %s" % (kind, counts[kind], n))
        elif not counts[token]:
            bad.append("%s пропал" % token)
    if bad:
        raise RuntimeError("предметы после боя: " + "; ".join(bad) + " (" + note + ")")
    return note


SAVE_CHECKS = {"battle_slots": check_battle_slots, "inventory": check_inventory}


# ------------------------------------------------------------------ действие

def manual_battle(a: dict, calib: dict, out_dir: Path, tag: str) -> str:
    """Бой, который играет ПОЛЬЗОВАТЕЛЬ: драйвер доводит, печатает инструкцию и ждёт.

    Отличий от `do_attack` два, и оба обязательны. Автобой (`A`) НЕ нажимается: ИИ зелья не
    пьёт, а применения предмета у него нет вовсе — нажать `A` значило бы получить пятый подряд
    бой без единого предмета. И потолок ожидания берётся из маршрута (600 с против 240 у
    автобоя): человек играет медленно, а обрыв на середине боя стоит всей арены.
    """
    import corpus_f6_run as run                      # поздний импорт: модуль зовёт нас первым
    off = run.mr.mark()
    print()
    print("  >>> %s" % a["text"])
    print("  >>> драйвер шагнёт на врага и будет ждать конца боя до %.0f с"
          % float(a.get("limit", 600)))
    run.mr.play_turn(tuple(a["target"]), calib, wait_ev="battle_start")
    ends = run.wait_event("battle_end", off, float(a.get("limit", 600)))
    if not ends:
        raise RuntimeError("бой %s не кончился за %.0f с: battle_end в журнал не пришёл"
                           % (a.get("kind"), float(a.get("limit", 600))))
    run.ap.key("escape")                             # итоги боя -> карта
    time.sleep(2.0)
    run.ensure_map()
    counts = Counter(e.get("ev") for e in run.mr.events_since(off))
    note = "бой %s: победитель %s, " % (a.get("kind"), ends[-1].get("winner")) + " ".join(
        "%s=%d" % (k, counts.get(k, 0)) for k in ("attack_new", "attack_get", "dmg", "round"))
    verify = a.get("verify")
    if verify:
        save = run.mr.quick_save(out_dir / ("probe-%s.sg" % tag))
        note += "; " + SAVE_CHECKS[verify["kind"]](sg.Sg.read(save), verify["id"])
    return note


# -------------------------------------------------------------- счётчики
#
# Пороги здесь не на глаз, а по двум измеренным величинам.
#
# * `dmg_buffs` с `random: false` даёт ТОЧНЫЙ урон юнита после модификаторов (живой журнал
#   `corpus/20260909-arena2`: King's Guard 60 ровно, 12 и 15 раз подряд); та же строка с
#   `random: true` — уже с разбросом (60..65). Поэтому счётчик смотрит только детерминированные
#   строки, а случайные печатает справочно.
# * Единственное, что кроме зелья может поднять урон и точность нашего юнита за прогон, — это
#   рост уровня: `GDynUpgr` даёт King's Guard +10 урона (60 -> 70, +17 %) и +1 точности
#   (80 -> 81, +1,25 %). Зелья дают +30 % (`GmodifL`: 78 и 104). Порог 1,25 разделяет эти два
#   случая с запасом и назван числом, а не «на глазок».
# * Прирост HP от роста уровня у наших юнитов — не больше 15 (`GDynUpgr.HIT_POINT`), а
#   наименьшее лечебное зелье ванили — 50. Лекарей и вампиризма на карте нет (сторожа
#   `items_plan`), так что прирост от 50 может быть только зельем.

MIN_HEAL = 50          # наименьший HP_POTION ванили
REPORT_HP = 10         # прирост, который печатаем справочно, но лечением не считаем
BOOST_RATIO = 1.25     # во сколько раз должен вырасти показатель, чтобы это было зелье


def _events(journal: Path, names: tuple[str, ...]):
    with journal.open("r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if not any('"%s"' % n in line for n in names):
                continue
            try:
                e = json.loads(line)
            except ValueError:
                continue
            if e.get("ev") in names:
                yield e


def scan(journal: Path) -> dict:
    """Один проход по журналу: урон с модификаторами, точность, рост HP и атаки предметов."""
    names = ("battle_units", "dmg_buffs", "power", "attack_new") + SNAPSHOT_EVENTS
    types: dict[str, str] = {}
    dmg_fixed: dict[str, Counter] = defaultdict(Counter)
    dmg_rand: dict[str, Counter] = defaultdict(Counter)
    powers: dict[str, Counter] = defaultdict(Counter)
    heals: list[tuple[str, int]] = []
    item_hits: Counter = Counter()
    last_hp: dict[str, int] = {}
    for e in _events(journal, names):
        ev = e.get("ev")
        if ev == "battle_units":
            for u in e.get("units", []):
                if u.get("type"):
                    types[str(u["id"])] = str(u["type"])
        elif ev == "dmg_buffs":
            box = dmg_rand if e.get("random") else dmg_fixed
            box[str(e.get("unit"))][int(e.get("ret", -1))] += 1
        elif ev == "power":
            powers[str(e.get("unit"))][int(e.get("ret", -1))] += 1
        elif ev == "attack_new":
            for key in ("disp_class", "class"):
                v = e.get(key)
                if isinstance(v, int) and v > 0:
                    if v in ip.ITEM_CLASSES:
                        item_hits[v] += 1
                    break
        if ev in SNAPSHOT_EVENTS:
            for u in e.get("units", []):
                uid, hp = str(u.get("id")), int(u.get("hp", 0))
                grew = hp - last_hp.get(uid, hp)
                if grew >= REPORT_HP:
                    heals.append((uid, grew))
                last_hp[uid] = hp
            if ev == "battle_end":
                last_hp.clear()      # между боями HP растёт регенерацией карты, это не зелье
    return {"types": types, "dmg_fixed": dmg_fixed, "dmg_rand": dmg_rand, "powers": powers,
            "heals": heals, "item_hits": item_hits}


def boosted(box: dict[str, Counter]) -> dict[str, tuple[int, int]]:
    """Юниты, у которых показатель вырос в BOOST_RATIO раз и больше: (минимум, максимум).

    База берётся из САМОГО прогона — наименьшее значение того же юнита. Именно поэтому
    инструкция велит первый раунд боя ничего не применять: без базы сравнивать не с чем.
    """
    out = {}
    for uid, vals in box.items():
        lo, hi = min(vals), max(vals)
        if lo > 0 and hi >= BOOST_RATIO * lo:
            out[uid] = (lo, hi)
    return out


def report_items(route: dict, journal: Path | None) -> bool:
    """«Ожидалось / получено» по следам применения предмета. Ноль — провал, названный поимённо."""
    want = route.get("expect_items")
    if not want:
        return True
    if journal is None or not journal.exists():
        print("журнала нет — применения предметов сверять не с чем")
        return False
    s = scan(journal)

    def who(uid: str) -> str:
        return "%s (%s)" % (uid, s["types"].get(uid, "тип неизвестен"))

    grew_dmg = boosted(s["dmg_fixed"])
    grew_pow = boosted(s["powers"])
    healed = [(u, g) for u, g in s["heals"] if g >= MIN_HEAL]
    got = {"boost_damage": len(grew_dmg), "boost_power": len(grew_pow),
           "heal": len(healed), "item_attack": sum(s["item_hits"].values())}
    titles = {"boost_damage": "зелье силы: dmg_buffs.ret (random=false) вырос в %.2f раза" % BOOST_RATIO,
              "boost_power": "зелье точности: power.ret вырос в %.2f раза" % BOOST_RATIO,
              "heal": "лечебные зелья: прирост hp от %d за один снимок" % MIN_HEAL,
              "item_attack": "сфера и талисман: attack_new классов %s" % sorted(ip.ITEM_CLASSES)}
    print()
    print("%-56s %10s %10s" % ("след применения предмета", "ожидалось", "получено"))
    ok = True
    for key in ("boost_damage", "boost_power", "heal", "item_attack"):
        need = int(want.get(key, 0))
        mark = "ок" if got[key] >= need else ("МАЛО" if got[key] else "НЕТ НИ ОДНОГО")
        ok = ok and got[key] >= need
        print("%-56s %10d %10d  %s" % (titles[key], need, got[key], mark))
    for uid, (lo, hi) in grew_dmg.items():
        print("  урон %s: %d -> %d (все значения %s)"
              % (who(uid), lo, hi, sorted(s["dmg_fixed"][uid])))
    for uid, (lo, hi) in grew_pow.items():
        print("  точность %s: %d -> %d (все значения %s)"
              % (who(uid), lo, hi, sorted(s["powers"][uid])))
    if s["heals"]:
        print("  прирост hp (от %d, лечением считаем от %d): " % (REPORT_HP, MIN_HEAL)
              + ", ".join("%s +%d" % (who(u), g) for u, g in s["heals"]))
    if s["item_hits"]:
        print("  атаки предметов: " + ", ".join(
            "%s(%d)=%d" % (ip.ITEM_CLASSES[c], c, n) for c, n in sorted(s["item_hits"].items())))
    print("  ВОСКРЕШЕНИЕ журналом не меряется вовсе: смотрите строку шага manual/inventory — "
          "павший юнит с 0 HP и исчезнувшее %s" % ip.ITEM_REVIVE)
    return ok


# ------------------------------------------------------------------ selftest

def selftest(path: Path) -> int:
    """Сверки по сохранению — на собранной карте и на её ПОРЧЕНОЙ копии. Игра не нужна.

        python tools/corpus_items.py [tools/../../disciples-modding/game-dev/scenarios/items.sg]

    Проверяется ровно то, ради чего сверки написаны: на целой карте они проходят, а стоит
    убрать талисман — падают с внятным текстом. «Упало бы» надо доказать, а не пообещать.
    """
    scen = sg.Sg.read(path)
    cases: list[tuple[str, bool, str]] = []

    def run(name: str, fn, want_ok: bool) -> None:
        try:
            cases.append((name, want_ok, "прошло: " + fn()))
        except (RuntimeError, KeyError, ValueError) as e:
            cases.append((name, not want_ok, "упало: %s" % e))

    run("пара боя 1 у отряда есть",
        lambda: check_battle_slots(scen, "%s,%s" % (ip.ITEM_STRENGTH, ip.ITEM_ACCURACY)), True)
    run("отчёт по инвентарю и HP", lambda: check_inventory(scen, "?"), True)
    run("сфера ещё цела — проверка «после боя» обязана упасть",
        lambda: check_inventory(scen, "-%s,%s*2" % (ip.ITEM_ORB, ip.ITEM_TALISMAN)), False)

    fought = _without(scen, ip.ITEM_ORB)      # копия карты, как после победного боя со сферой
    run("после боя: сферы нет, талисманов два",
        lambda: check_inventory(fought, "-%s,%s*2" % (ip.ITEM_ORB, ip.ITEM_TALISMAN)), True)
    run("вещи нет вовсе — сверка слотов обязана упасть",
        lambda: check_battle_slots(fought, ip.ITEM_ORB), False)

    bad = 0
    for name, ok, note in cases:
        print("%-52s %s  %s" % (name, "ок" if ok else "ПРОВАЛ", note[:110]))
        bad += not ok
    print("selftest сверок: %d из %d" % (len(cases) - bad, len(cases)))
    return 1 if bad else 0


def _without(scen: sg.Sg, kind: str) -> sg.Sg:
    """Копия карты, из инвентаря лидера убраны все вещи типа `kind` (как их съел бы бой)."""
    from dataclasses import replace
    kinds = {r.obj_id.upper(): sg.fields(r.body, "ITEM_ID:s ITEM_TYPE:s")["ITEM_TYPE"]
             for r in scen.find(WHAT_ITEM)}
    player = f6.human_player(scen)["PLAYER_ID"]
    rec = next(r for r in scen.find(sg.WHAT_STACK)
               if sg.parse_stack(r.body)["OWNER"] == player)
    c = sg.Cursor(rec.body)
    c.fields(sg._GROUP)
    head = c.pos
    keep = [i for i in (c.str("ITEM_ID") for _ in range(c.count()))
            if kinds.get(i.upper()) != kind]
    body = (rec.body[:head + 10] + struct.pack("<I", len(keep))
            + b"".join(sg.f_str("ITEM_ID", i) for i in keep) + rec.body[c.pos:])
    return scen.with_record(rec, replace(rec, body=body))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    default = (Path(__file__).resolve().parents[2] / "disciples-modding" / "game-dev"
               / "scenarios" / "items.sg")
    sys.exit(selftest(Path(sys.argv[1]) if len(sys.argv) > 1 else default))
