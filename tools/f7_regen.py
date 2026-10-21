#!/usr/bin/env python3
"""Карта M16 `f7-m16-regen`: дневной тик ВНУТРИ укрепления — числа, таблица гипотез и сторожа.

Остаток S8, единственный незакрытый замер Ф7. Формула регенерации в поле выведена вторым
прогоном и держится рангом 1:

    за один дневной тик:  HP += ⌊ HIT_POINT × (Gunits.REGEN + REGEN_LWAR·[лорд Воитель]) / 100 ⌋

Открытый вопрос ровно один: **что добавляет укрепление**. `GcityInf.REGEN_F` = 10/15/20/25/30
по уровням города, `Grace.REGEN_H` = 35 у столицы. Не установлено, складывается ли это с
собственной регенерацией юнита или заменяет её. Второй прогон видел в столице 40 % при
табличных 35 — разница ровно в собственные 5 %, но наблюдение было одно.

Карта даёт **шесть точек за один ход**: столица плюс пять городов уровней 1…5, у каждого
одинаковый отряд с одинаковыми ранами. Единственная переменная — уровень укрепления.

Три гипотезы, которые карта различает:

| ключ | как считает один тик внутри укрепления |
|---|---|
| `складывается` | `⌊HP × (REGEN + REGEN_F + бонус лорда) / 100⌋` — одно округление на сумму |
| `заменяет`     | `⌊HP × (REGEN_F + бонус лорда) / 100⌋` — собственная регенерация не в счёт |
| `два тика`     | `⌊HP × (REGEN + бонус) / 100⌋ + ⌊HP × REGEN_F / 100⌋` — два отдельных округления |

Первые две обязаны различаться на КАЖДОЙ строке — ради этого карта и делается, и это
проверяет сторож. Третью различает лидер с `HIT_POINT = 150` в столице под Магом
(60 против 59): половинки при 5 % от 150 живут только там.

Профиль карты (состав, места, казна) лежит в `f7_plan.PROFILES["m16"]` вместе с остальными
восемью — здесь только числа и сторожа. Игра отсюда не запускается.
"""
from __future__ import annotations

import functools
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import make_c4_map as c4  # noqa: E402
import make_city_map as city  # noqa: E402
import scenario_squads as sq  # noqa: E402
from dbf_dump import read as dbf_read  # noqa: E402
from f7_wave2 import (  # noqa: E402
    CAP, MAP_SIZE, PLAIN_COST, RACE_EMPIRE, SQUIRE, cheb, grace, gvars)

# ------------------------------------------------------------------- решения карты

REGEN_LEADER = "g000uu0019"   # Pegasus Knight: LEADER_CAT 1, HIT_POINT 150, MOVE 20
REGEN_WOUND = 1               # рана в один HP: глубже некуда, а ноль — это ПАВШИЙ, он не лечится
ENTRY_STEPS = 1               # отряд ровно в шаге от входа: так стоял отряд M4, и он вошёл
MARGIN_MAIN = 0.40            # запас до потолка здоровья под лордом прогона
MARGIN_WORST = 0.20           # то же в худшем случае — под Воителем, у которого +15 к тику

LORD_MAGE, LORD_WARRIOR, LORD_THIEF = 0, 1, 2   # Glord.CATEGORY
MAIN_LORD = LORD_MAGE         # прогон играется Магом: см. `why_mage` в докстринге модуля
WORST_LORD = LORD_WARRIOR

# Пятна укреплений. Города отнесены от столицы не ближе `CAPTURE_CLEAN_GAP`: иначе пятно
# столицы дорастает до города и мешает — ровно это испортило замер земли на M3.
FORTS: list[dict] = [
    {"key": "capital", "tier": None, "xy": CAP, "size": c4.CAPITAL_SIZE, "title": "столица"},
    {"key": "city1", "tier": 1, "xy": (20, 5), "size": city.VILLAGE_TILES, "title": "город 1"},
    {"key": "city2", "tier": 2, "xy": (20, 15), "size": city.VILLAGE_TILES, "title": "город 2"},
    {"key": "city3", "tier": 3, "xy": (20, 25), "size": city.VILLAGE_TILES, "title": "город 3"},
    {"key": "city4", "tier": 4, "xy": (20, 35), "size": city.VILLAGE_TILES, "title": "город 4"},
    {"key": "city5", "tier": 5, "xy": (32, 20), "size": city.VILLAGE_TILES, "title": "город 5"},
]
FORT_GAP = 6                  # между пятнами городов: чтобы отряды не путались, чей это вход
CITY_CLEAN_GAP = 10           # от пятна столицы до пятна города


def entrance(fort: dict) -> tuple[int, int]:
    """Вход укрепления — правый нижний угол пятна, `позиция + размер − 1`.

    Ранг 1: `getFortOrRuinEntrance` 0x4cd253 для типа 0x1d (крепость) зовёт 0x5e94eb, тело
    возвращает `(a.x + b.x − 1, a.y + b.y − 1)` от позиции и размера. Столица и город — один
    и тот же тип объекта, отдельного правила у города нет. Живьём это же видно на M4: отряд
    стоял в (10,10), вход столицы (9,9), и он вошёл (`INSIDE = S143FT0000`).
    """
    return fort["xy"][0] + fort["size"] - 1, fort["xy"][1] + fort["size"] - 1


def party_xy(fort: dict) -> tuple[int, int]:
    """Клетка отряда — на шаг по диагонали наружу от входа, как (10,10) от (9,9) на M4."""
    x, y = entrance(fort)
    return x + ENTRY_STEPS, y + ENTRY_STEPS


def fort_tiles(fort: dict) -> list[tuple[int, int]]:
    x0, y0 = fort["xy"]
    return [(x0 + dx, y0 + dy) for dy in range(fort["size"]) for dx in range(fort["size"])]


def cities() -> list[dict]:
    return [f for f in FORTS if f["tier"] is not None]


def prof_forts(prof: dict) -> list[dict]:
    """Укрепления, как их задаёт ПРОФИЛЬ, а не таблица `FORTS` выше.

    Сторожа обязаны читать данные карты: иначе порча профиля в `--selftest` пройдёт мимо них,
    и «упало бы» останется обещанием. `FORTS` — только заготовка, из которой профиль собран.
    """
    out = [{"tier": None, "xy": tuple(CAP), "size": c4.CAPITAL_SIZE, "title": "столица"}]
    out += [{"tier": int(v["tier"]), "xy": tuple(v["xy"]), "size": city.VILLAGE_TILES,
             "title": "город %d" % int(v["tier"])} for v in prof["villages"]]
    return out


# ------------------------------------------------- проценты укрепления из таблиц

@functools.lru_cache(maxsize=None)
def city_regen(tier: int, race_id: str = RACE_EMPIRE) -> int:
    """`GcityInf.REGEN_F` — 10/15/20/25/30 по уровням 1…5. Раса берётся ЗАХВАТЧИКА: город
    к моменту замера уже наш. Значения у всех рас одинаковы, так что выбор ни на что не влияет.
    """
    for row in dbf_read(sq.GLOBALS / "GcityInf.dbf")["rows"]:
        if row["RACE_ID"].lower() == race_id.lower() and int(row["SIZE"]) == tier:
            return int(row["REGEN_F"])
    raise SystemExit("в GcityInf.dbf нет города уровня %d у расы %s" % (tier, race_id))


def fort_regen(fort: dict) -> int:
    """Процент укрепления: столица — `Grace.REGEN_H`, город — `GcityInf.REGEN_F` по уровню."""
    if fort["tier"] is None:
        return int(grace(RACE_EMPIRE)["REGEN_H"])
    return city_regen(int(fort["tier"]))


def lord_bonus(category: int) -> int:
    """`GVars.REGEN_LWAR` достаётся только лорду категории 1 (Воитель) — ранг 1 по run2."""
    return int(gvars()["REGEN_LWAR"]) if category == LORD_WARRIOR else 0


def tick(hp_max: int, percent: int) -> int:
    """Один дневной тик: округление ВНИЗ, и проценты складываются ДО него (run2 §4.2)."""
    return hp_max * percent // 100


# ------------------------------------------------------------- таблица ожидаемых чисел

def unit_points(t: sq.Tables, prof: dict) -> list[tuple[str, int, int, int]]:
    """(имя, максимум HP, `Gunits.REGEN`, заданная сценарием рана) по каждому юниту отряда.

    Отряды карты одинаковы, поэтому берётся первый: `check_parties_same` уже доказал, что
    остальные пять от него не отличаются. Рана берётся ИЗ ПРОФИЛЯ, а не из константы: иначе
    сторож запаса проверял бы задуманное, а не записанное.
    """
    p = prof["parties"][0]
    pairs = [(p["leader"], p.get("leader_hp"))] + list(p["units"])
    out = []
    for unit_id, hp in pairs:
        hp_max = int(t.unit(unit_id)["HIT_POINT"])
        out.append((t.name(unit_id), hp_max, int(t.unit(unit_id)["REGEN"] or 0),
                    hp_max if hp is None else int(hp)))
    return out


def hypothesis_rows(forts: list[dict], points: list[tuple[str, int, int, int]],
                    lord: int) -> list[dict]:
    """Строка на каждую пару «укрепление × юнит»: сколько HP придёт за один тик по трём
    гипотезам. «к замеру» — здоровье в момент замера: рана плюс тик в ПОЛЕ, который движок
    делает уже при загрузке карты (run2 §4.2, `income` зовётся и на `turn = 1`)."""
    bonus = lord_bonus(lord)
    rows = []
    for fort in forts:
        f = fort_regen(fort)
        for name, hp_max, regen, wound in points:
            rows.append({
                "укрепление": fort["title"], "юнит": name, "максимум": hp_max,
                "% укрепления": f, "к замеру": min(wound + tick(hp_max, regen + bonus), hp_max),
                "складывается": tick(hp_max, regen + f + bonus),
                "заменяет": tick(hp_max, f + bonus),
                "два тика": tick(hp_max, regen + bonus) + tick(hp_max, f),
            })
    return rows


# ------------------------------------------------------------------------ сторожа

def check_forts(prof: dict, key: str) -> None:
    """Шесть укреплений: одна столица, пять городов уровней ровно 1…5, гарнизоны пусты."""
    tiers = sorted(int(v["tier"]) for v in prof["villages"])
    if tiers != [1, 2, 3, 4, 5]:
        raise SystemExit("%s: уровни городов %s, нужны ровно 1…5 — единственная переменная "
                         "замера это уровень укрепления, повтор или пропуск её ломает"
                         % (key, tiers))
    for v in prof["villages"]:
        if v.get("garrison"):
            raise SystemExit("%s: у города %s гарнизон %s — пользователь входит и захватывает "
                             "БЕЗ боя, бой сбил бы и здоровье, и ход"
                             % (key, tuple(v["xy"]), list(v["garrison"])))
        if v.get("fight"):
            raise SystemExit("%s: город %s помечен под бой" % (key, tuple(v["xy"])))
    if prof["neutrals"]:
        raise SystemExit("%s: на карте %d нейтральных отрядов, а врагов быть не должно"
                         % (key, len(prof["neutrals"])))
    towns = [f for f in prof_forts(prof) if f["tier"] is not None]
    for a in towns:
        gap = min(cheb(x, y) for x in fort_tiles(a) for y in c4.capital_tiles())
        if gap < CITY_CLEAN_GAP:
            raise SystemExit("%s: город %s в %d клетках от столицы при нужных %d — пятно "
                             "столицы дорастёт до города, как на M3"
                             % (key, a["xy"], gap, CITY_CLEAN_GAP))
        for b in towns:
            if b is a:
                continue
            d = min(cheb(x, y) for x in fort_tiles(a) for y in fort_tiles(b))
            if d < FORT_GAP:
                raise SystemExit("%s: города %s и %s в %d клетках друг от друга при нужных %d"
                                 % (key, a["xy"], b["xy"], d, FORT_GAP))


def check_parties_same(prof: dict, key: str) -> None:
    """Шесть отрядов, одинаковых до последней раны: иначе строки таблицы несравнимы."""
    parties, forts = prof["parties"], prof_forts(prof)
    if len(parties) != len(forts):
        raise SystemExit("%s: отрядов %d при %d укреплениях — на каждое нужен свой"
                         % (key, len(parties), len(forts)))
    shapes = {(p["leader"].lower(), p.get("leader_hp"), tuple(sorted(
        (u.lower(), hp) for u, hp in p["units"]))) for p in parties}
    if len(shapes) != 1:
        raise SystemExit("%s: отряды различаются составом или ранами (%d разных): сравнивать "
                         "укрепления между собой станет нечем" % (key, len(shapes)))
    first = parties[0]
    hps = [first.get("leader_hp")] + [hp for _, hp in first["units"]]
    if any(hp is None or hp <= 0 for hp in hps):
        raise SystemExit("%s: у кого-то в отряде здоровье %s — ноль это ПАВШИЙ, он не лечится "
                         "вовсе, а None это полное здоровье и упор в потолок" % (key, hps))


def check_entrances(t: sq.Tables, prof: dict, key: str) -> None:
    """Каждый отряд ровно в шаге от СВОЕГО входа, и шаг оплачен очками хода лидера."""
    places = {tuple(p["xy"]): p for p in prof["parties"]}
    for fort in prof_forts(prof):
        want = party_xy(fort)
        p = places.get(want)
        if p is None:
            raise SystemExit("%s: у %s (вход %s) нет отряда в %s — отряды стоят в %s"
                             % (key, fort["title"], entrance(fort), want, sorted(places)))
        d = cheb(want, entrance(fort))
        move = int(t.unit(p["leader"])["MOVE"] or 0)
        if d != ENTRY_STEPS:
            raise SystemExit("%s: отряд %s в %d шагах от входа %s, нужен ровно %d"
                             % (key, want, d, entrance(fort), ENTRY_STEPS))
        if PLAIN_COST * d > move:
            raise SystemExit("%s: шаг до входа %s стоит %d очков при %d у лидера — за один ход "
                             "не войти" % (key, entrance(fort), PLAIN_COST * d, move))
        if not (0 <= want[0] < MAP_SIZE and 0 <= want[1] < MAP_SIZE):
            raise SystemExit("%s: отряд %s вне карты %dx%d" % (key, want, MAP_SIZE, MAP_SIZE))


def check_headroom(t: sq.Tables, prof: dict, key: str) -> None:
    """После тика внутри укрепления никто не упирается в потолок здоровья — с запасом.

    Считается самая щедрая из гипотез («складывается»), и дважды: под лордом прогона и под
    Воителем, у которого тик на `REGEN_LWAR` больше. Запас под Воителем меньше — потому
    основной проход и играется Магом.
    """
    for lord, margin in ((MAIN_LORD, MARGIN_MAIN), (WORST_LORD, MARGIN_WORST)):
        for r in hypothesis_rows(prof_forts(prof), unit_points(t, prof), lord):
            after = r["к замеру"] + r["складывается"]
            room = r["максимум"] - after
            if room < margin * r["максимум"]:
                raise SystemExit(
                    "%s: %s в %s под лордом категории %d приходит к %d/%d, запас %d = %.0f %% "
                    "при нужных %.0f %% — рана мелка, тик упрётся в потолок и замер испорчен"
                    % (key, r["юнит"], r["укрепление"], lord, after, r["максимум"], room,
                       100.0 * room / r["максимум"], 100.0 * margin))


def check_hypotheses(rows: list[dict], key: str) -> None:
    """Ради этого карта и делается: «складывается» и «заменяет» обязаны давать РАЗНЫЕ числа
    на каждой строке. Совпадение хоть в одной — строка ничего не меряет."""
    if not rows:
        raise SystemExit("%s: таблица гипотез пуста — сравнивать нечего" % key)
    same = [r for r in rows if r["складывается"] == r["заменяет"]]
    if same:
        r = same[0]
        raise SystemExit("%s: на строке «%s / %s» обе гипотезы дают %d — различить их нечем "
                         "(%d строк из %d совпали); нужен другой юнит или другая рана"
                         % (key, r["укрепление"], r["юнит"], r["складывается"],
                            len(same), len(rows)))
    if not any(r["складывается"] != r["два тика"] for r in rows):
        raise SystemExit("%s: ни одна строка не отличает «одно округление на сумму» от «двух "
                         "отдельных» — половинку процента даёт только нечётный HIT_POINT" % key)


def check_regen(t: sq.Tables, prof: dict, key: str) -> None:
    """Все сторожа карты M16 разом — вызывается из `f7_plan.check_profile`."""
    if not prof.get("villages"):
        return
    check_forts(prof, key)
    check_parties_same(prof, key)
    check_entrances(t, prof, key)
    check_headroom(t, prof, key)
    check_hypotheses(hypothesis_rows(prof_forts(prof), unit_points(t, prof), MAIN_LORD), key)


# ------------------------------------------------------------------------ печать

def print_regen(t: sq.Tables, prof: dict) -> None:
    if not prof.get("villages"):
        return
    for fort in prof_forts(prof):
        print("   %-9s пятно %s %dx%d, вход %s, отряд %s, %% укрепления %d"
              % (fort["title"], tuple(fort["xy"]), fort["size"], fort["size"],
                 entrance(fort), party_xy(fort), fort_regen(fort)))
    for lord, title in ((MAIN_LORD, "Маг (прогон)"), (WORST_LORD, "Воитель (второй проход)")):
        print("   лорд %s, бонус к тику %d %%:" % (title, lord_bonus(lord)))
        print("   %-9s %-16s %6s %5s %8s %9s %8s %8s"
              % ("укрепл.", "юнит", "макс", "%укр", "к замеру", "склад.", "заменяет", "два тика"))
        for r in hypothesis_rows(prof_forts(prof), unit_points(t, prof), lord):
            print("   %-9s %-16s %6d %5d %8d %9d %8d %8d"
                  % (r["укрепление"], r["юнит"], r["максимум"], r["% укрепления"],
                     r["к замеру"], r["складывается"], r["заменяет"], r["два тика"]))


# --------------------------------------------------------------------- selftest

def regen_cases(t: sq.Tables, prof: dict, raises) -> list[tuple[str, str | None]]:
    """Порча ровно одного обещания за раз. `raises` — ловилка из `f7_plan`."""
    far = dict(prof, parties=[dict(prof["parties"][0], xy=(44, 44))] + prof["parties"][1:])
    mixed = dict(prof, parties=[dict(prof["parties"][0], leader_hp=None)] + prof["parties"][1:])
    shallow = dict(prof, parties=[dict(p, leader_hp=140, units=[(SQUIRE, 90)])
                                  for p in prof["parties"]])
    dupe = dict(prof, villages=[dict(v, tier=3) for v in prof["villages"]])
    manned = dict(prof, villages=[dict(v, garrison=("g000uu5017",)) if i == 0 else v
                                  for i, v in enumerate(prof["villages"])])
    near = dict(prof, villages=[dict(v, xy=(12, 6)) if i == 0 else v
                                for i, v in enumerate(prof["villages"])])
    return [
        ("уровни городов не 1…5", raises(lambda: check_forts(dupe, "m16"))),
        ("гарнизон города непуст", raises(lambda: check_forts(manned, "m16"))),
        ("город придвинут к столице", raises(lambda: check_forts(near, "m16"))),
        ("на карте появился враг", raises(
            lambda: check_forts(dict(prof, neutrals=[{"xy": (40, 40)}]), "m16"))),
        ("отряды с разными ранами", raises(lambda: check_parties_same(mixed, "m16"))),
        ("отрядов меньше, чем укреплений", raises(
            lambda: check_parties_same(dict(prof, parties=prof["parties"][:3]), "m16"))),
        ("отряд не доходит до укрепления", raises(lambda: check_entrances(t, far, "m16"))),
        ("раны мелки — упор в потолок", raises(lambda: check_headroom(t, shallow, "m16"))),
        ("гипотезы совпали на строке", raises(lambda: check_hypotheses(
            hypothesis_rows(prof_forts(prof), [("подсадной", 100, 0, 1)],
                            MAIN_LORD), "m16"))),
        ("таблица гипотез пуста", raises(lambda: check_hypotheses([], "m16"))),
    ]
