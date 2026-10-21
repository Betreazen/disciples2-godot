#!/usr/bin/env python3
"""Генератор карты живого прогона Ф6: `f6_route.sg` и маршрут `f6_route.json`.

    python tools/make_f6_map.py
    python tools/make_f6_map.py --out ../disciples-modding/game-dev/scenarios/f6_route.sg \
        --route tools/f6_route.json

**Карта — вариант карты Ф5, а не новая.** Грунты, столица (5,5), пеший герой и жила берутся
из `make_c4_map` без единого изменения: рост земли игрока (задача 12) сверяется с уже снятыми
сохранениями `corpus/20260907-c4/save-*.sg`, а он считается от столицы, и любая правка грунта
или положения столицы сделала бы сравнение невозможным. Маршрут при этом ДРУГОЙ — и это
намеренно: одинаковый рост земли на двух разных маршрутах отделит правило «от столицы наружу»
от правила «вокруг пройденных клеток».

Что Ф6 добавляет поверх карты Ф5 (всё — записи `.sg`, ни одного нового класса движка):

1. **Отряд игрока из вынужденного списка** (план Ф6, задача 13): пять классов атак держатся на
   одном юните каждый, и без них класс не получит в корпусе ни одного случая никогда.
2. **Казна** — щедрая, но далеко от потолка `Economy.CurrencyMax = 9999` (ранг 1, `0x586642`):
   упереться в потолок значит своими руками обрезать дневной доход, ради которого прогон и
   нужен. См. `BANK`.
3. **Построек в карту не пишется НИ ОДНОЙ.** Предзаданный `CPlayerBuildings` бесполезен: игра
   при старте сцены пересоздаёт игрока по выбранному лорду (та же правка, что подменяет героя,
   см. ниже) и список построек обнуляет — в живой игре окно построек показывало только здания
   с `REQUIRED = g000000000`. Поэтому целью прогона взято здание, доступное с нулевой столицы:
   Библиотека `G000BB0012` (200 золота), а наймом — `Маг` `g000uu0009` (260 золота), которого
   она открывает. Конкретное здание значения не имеет — нужны факт постройки (`build_status`),
   факт найма (`hire_list`) и трата из казны.
4. **Известные заклинания**: `CPlayerKnownSpells` получает девять заклинаний восьми категорий
   `Lspell` — вся магия в игре применяется на карте (ранг 1, задача 9), значит и проверяется
   она здесь, диффом `.sg`, а не журналом боя.
5. **Объекты**: лавка, лагерь наёмников, наставник и руина с охраной — три события `site` и
   один бой. Раскладки тел сняты с ванильной карты `Exports-archive` (см. `site_body`).
6. **Нейтральные отряды** — два, и оба ОТСТАВЛЕНЫ от линии маршрута (`NEUTRAL_AT`). Вплотную
   к выходу из столицы они стояли до 08.09.2026, и это оборвало прогон на первом же ходу:
   бой пришёл сам, отряд поддержки его проиграл, карта осталась неотснятой. Теперь все три боя
   (руина и два отряда) стоят ХВОСТОМ маршрута, после 45 карточных ходов, слабейший первый —
   расстановку и проверку зазора числом делает `f6_route.build_actions`.

**Чего генератор НЕ делает и почему.** Тип и уровень героя не задаются: игра при старте новой
сцены заменяет лидера стартового отряда на героя выбранного лорда (сверка `map-start.sg` против
`save-10.sg` прогона Ф5: `S143UN0001` -> `S143UN0002`, лорд `G000LR0001` -> `G000LR0003`).
Казна, наоборот, сохраняется (`G0100` -> `G1200` за 11 дней) — поэтому её задавать можно.

**Отряд старт сцены тоже НЕ переживает — измерено 08.09.2026.** В собранной карте стек
`S143KC0000` держит лидера и всех пять вынужденных солдат (разбор `.sg` это подтверждает), а в
живой игре после выбора лорда герой стоял один при «Лидерство 0/3»: пересоздание игрока уносит
и солдат. Поэтому боевой прогон стартует не с этой карты, а с **патченого сохранения** —
`tools/patch_f6_save.py`. Карта остаётся тем, чем была: источником грунтов, столицы, объектов,
нейтралов и маршрута, и файлом, который пользователь один раз загружает, чтобы сделать `Q`.
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

MODDING = Path(__file__).resolve().parents[2] / "disciples-modding"
OUT = MODDING / "game-dev" / "scenarios" / "f6_route.sg"
ROUTE_OUT = Path(__file__).with_name("f6_route.json")

GATE = (c4.CAPITAL_XY[0] + c4.CAPITAL_SIZE, c4.CAPITAL_XY[1] + c4.CAPITAL_SIZE)   # (10,10)

# --------------------------------------------------------------------- казна

# Потолок любой валюты — 9999 (`Economy.CurrencyMax`, ранг 1: каждое сложение зажимается в
# [0,9999], `0x586642`/`0x586a6f`). Дневной доход прогона Ф5 — 100 золота и 25 маны жизни;
# за 48 дней это +4800/+1200. Берём 3000: хватает на постройку (200) и найм (260) сразу, и к
# последнему дню казна ~7800 (3000 - 460 + 500 из руины + 4800) — ниже потолка, то есть доход
# ни разу не обрезан.
# Мана — по 3000 каждого вида: заклинания прогона стоят рунной, жизни, адской и рощи (у
# «Скорости» цена только в мане рощи), а обрезки не будет и там: 3000 + 25*48 = 4200.
BANK = "G3000:R3000:Y3000:E3000:W3000:B3000"

# --------------------------------------------------------------- отряд игрока

# Вынужденный список плана Ф6 (задача 13): по одному юниту на класс атаки во всей ванили.
# Уровень берётся из Gunits (3-4), боями не прокачивается — юнит ставится напрямую.
SQUAD = [
    "g000uu0150",   # L4 Патриарх      — Revive       (2-я атака g000aa1150)
    "g000uu0178",   # L4 Сущий         — DrainLevel   (2-я атака g000aa1178)
    "g000uu0161",   # L3 Алхимик       — GiveAttack
    "g000uu0171",   # L3 Двойник       — Doppelganger
    "g000uu0164",   # L4 Повелитель волков — TransformSelf
]

# Нейтралы: лидер шаблона (Разбойник) плюс солдаты с более частыми классами Ф6.
# Нейтралы — по ТРИ юнита, все 1–2 уровня. Это расчёт по живым прогонам 08.09.2026: наш отряд
# собран из поддержки (Revive, DrainLevel, GiveAttack, Doppelganger, TransformSelf), урона в нём
# почти нет, и полный отряд из пяти юнитов 1–4 уровня он проигрывал дважды (11:00 и 14:10, ход
# 53). Поражение обрывает прогон до нейтрала B, а с ним теряются Petrify, LowerInitiative, Drain
# и Summon. Классы сохранены, но у каждого взят самый слабый носитель (tools/re/class_units.py):
# Cure держится только на юнитах 3 уровня и потому снят — его снимет отдельный бой, если
# понадобится. Пользователь: «если нужно чтобы отряд не умирал, как-то поменять отряд нейтралов».
NEUTRAL_A = ["g000uu0067",   # L2 Ведьма        — TransformOther
             "g000uu8150",   # L1 Сэр Аллемон   — Shatter (2-я)
             "g000uu0033"]   # L1 Новичок       — BoostDamage
NEUTRAL_B = ["g000uu5363",   # L1 Отшельник     — LowerInitiative (2-я)
             "g000uu5025",   # L1 Медуза        — Petrify
             "g000uu0097",   # L1 Носферат      — Drain
             "g000uu6013"]   # L1 Оккультист    — Summon
# Охрана руины — ДВА юнита, и это расчёт, а не экономия. Пользователь 08.09.2026: чтобы
# Исцеление было чем мерить, нужен бой, который отряд ТОЧНО выиграет, но в котором потеряет
# часть HP. Наш отряд собран из поддержки (Revive, DrainLevel, GiveAttack, Doppelganger,
# TransformSelf), урона в нём почти нет, и полный отряд из шести он не осилил бы — прогон 11:00
# уже проиграл такой бой нейтралу и оборвался.
#   * Лучник — обычный Damage: он нанесёт урон, иначе лечить будет нечего и свиток «не возымеет
#     эффекта» повторится;
#   * Лесная дева — BestowWards: класс круга 4, ради событий `immune` от вардов.
# Остальные классы охраны переехали к нейтралам A и B, с которыми дерёмся последними — там
# поражение уже ничего не стоит.
RUIN_GUARD = ["g000uu0006",  # L1 Лучник       — Damage: чтобы отряд потерял HP
              "g000uu8133"]  # L1 Лесная дева  — BestowWards

# Клетки отрядов. Живой прогон 08.09.2026: `A` стояла на (11,9) — вплотную к цели первого хода
# (10,10). Бой начался на первом же ходу, отряд поддержки его проиграл, и прогон оборвался до
# того, как карта дала хоть один день. Теперь обе клетки отставлены так, что до КАЖДОЙ клетки
# карточной части маршрута — и до целей ходов, и до клеток пути между ними — не меньше двух:
# соседство в Disciples 2 это восемь клеток вокруг, значит зазор 2 и есть «не сосед». Число
# считает и печатает `f6_route.build_actions` (MIN_NEUTRAL_GAP); пересчёт обязателен при любой
# правке путевых точек. Одной клетки вправо от (11,9) не хватило: зазор остаётся 1, потому что
# путь из столицы идёт по соседнему ряду (измерено) — поэтому `A` ушла ещё и на две клетки
# вверх, а `B` — на три вправо от столбца x=34, по которому маршрут ходит четырежды.
NEUTRAL_AT = {"A": (12, 7), "B": (36, 13)}

# ------------------------------------------------------------------- постройки

# Предпостройки в сценарий НЕ пишутся: старт сцены пересоздаёт игрока и стирает их (см.
# докстринг, п. 3). Цель — первое здание ветки магов Империи, доступное без предпосылок.
TARGET_BUILDING = "G000BB0012"      # Библиотека: Gbuild REQUIRED g000000000, LEVEL 2, 200 золота
# Ученик: базовый юнит ветки магов, 60 золота, Gunits.UPGRADE_B = g000000000 — доступен всегда.
# РАНГ 1, живая игра 08.09.2026: в столице с построенной Библиотекой предлагается только Ученик,
# Мага купить нельзя. Значит `Gunits.UPGRADE_B` — это здание, нужное юниту КАК ЦЕЛИ УЛУЧШЕНИЯ, а
# не для найма: нанимают базового юнита ветки, а здания открывают, во что он вырастет. У Ученика
# поле пустое, у Мага оно указывает на Библиотеку — отсюда и прежняя ошибка «Библиотека открывает
# найм Мага». Для прогона разницы нет: `hire_list` рождается от клика по слоту при любом юните.
TARGET_HIRE = "g000uu0008"

# ------------------------------------------------------------------ заклинания

# По представителю категории Lspell, кроме трёх: L_FOG (6) и L_INVISIBILITY (9) прячут карту
# от нас же и сорвали бы следующие шаги маршрута, L_REMOVE_ROD (10) требует чужого жезла,
# которого на карте нет. Тринадцатой категории L_GIVE_WARDS (12) в ванили НЕТ НИ ОДНОГО
# заклинания — это измерено по Gspells, а не пропущено.
#
# Порядок внутри страницы уровня — РАНГ 3 (предполагается возрастание SPELL_ID); в маршруте он
# лежит отдельным полем `index`, и его правка не трогает ничего другого.
KNOWN_SPELLS = [
    "G000SS0004",   # L1 Молния            — L_ATTACK        (на нейтрала)
    "G000SS0064",   # L1 Слабость          — L_LOWER         (на нейтрала, модификатор)
    "G000SS0100",   # L1 Скорость          — L_RESTORE_MOVE  (два эффекта сразу: RESTORE_M 30 + MoveAllowance)
    "G000SS0007",   # L2 Исцеление         — L_HEAL          (HEAL_QTY 30)
    "G000SS0008",   # L2 Призывание: Живой Доспех — L_SUMMON
    "G000SS0009",   # L2 Истинное зрение   — L_UNFOG         (AREA 5)
    "G000SS0107",   # L3 Создание Рощи     — L_CHANGE_TERRAIN, GROUND_CAT 1, AREA 3
    "G000SS0017",   # L4 Призыв к оружию   — L_BOOST         (модификатор на свой отряд перед боем)
    "G000SS0120",   # L5 Создание Леса     — L_CHANGE_TERRAIN, GROUND_CAT 1, AREA 7 (квадрат против ромба)
]

# ---------------------------------------------------------------------- объекты

WHAT_MERCHANT = ".?AVCMidSiteMerchant@@"
WHAT_MERCS = ".?AVCMidSiteMercs@@"
WHAT_TRAINER = ".?AVCMidSiteTrainer@@"
WHAT_RUIN = ".?AVCMidRuin@@"
SITE_SPAN = 3                        # объект занимает 3x3 (измерено по плану ванильных карт)

# (класс, левый верхний угол, заголовок). IMG_ISO взят с ванильной карты: 2 у лавки и лагеря
# наёмников, 3 у наставника.
SITES = [
    (WHAT_MERCHANT, (13, 7), 2, "Лавка Ф6"),
    (WHAT_MERCS, (18, 7), 2, "Наёмники Ф6"),
    (WHAT_TRAINER, (23, 7), 3, "Наставник Ф6"),
]
MERCHANT_GOODS = [("G000IG0001", 10), ("G000IG0005", 10), ("G000IG0009", 1)]
MERCS_UNITS = [("G000UU0006", 1), ("G000UU0011", 1)]
RUIN_AT = (28, 7)
RUIN_CASH = "G0500:R0000:Y0000:E0000:W0000:B0000"
RUIN_ITEM = "G000IG0001"


def centre(corner: tuple[int, int]) -> tuple[int, int]:
    return corner[0] + SITE_SPAN // 2, corner[1] + SITE_SPAN // 2


def footprint(corner: tuple[int, int]) -> list[tuple[int, int]]:
    return [(corner[0] + dx, corner[1] + dy)
            for dx in range(SITE_SPAN) for dy in range(SITE_SPAN)]


# --------------------------------------------------------------- тела записей

def site_body(site_id: str, img_iso: int, title: str, desc: str,
              x: int, y: int, extra: bytes = b"") -> bytes:
    """Общая голова `site` из tools/ksy/sg.ksy плюс хвост класса плюс пустой список посетителей.

    Порядок и маркеры сверены с живой записью ванильной карты (`MidSiteMerchant` из
    «A Return To Simpler Times»): SITE_ID, IMG_ISO, IMG_INTF, TXT_TITLE, TXT_DESC, POS_X,
    POS_Y, VISITER, AIPRIORITY, <хвост класса>, <id> + u32 числа посетителей.
    """
    return (sg.f_str("SITE_ID", site_id) + sg.f_int("IMG_ISO", img_iso)
            + sg.f_str("IMG_INTF", "") + sg.f_str("TXT_TITLE", title)
            + sg.f_str("TXT_DESC", desc)
            + sg.f_int("POS_X", x) + sg.f_int("POS_Y", y)
            + sg.f_str("VISITER", sg.NULL_ID) + sg.f_int("AIPRIORITY", 3)
            + extra + sg.f_count(site_id, 0))


def merchant_tail() -> bytes:
    flags = ("BUY_ARMOR", "BUY_JEWEL", "BUY_WEAPON", "BUY_BANNER",
             "BUY_POTION", "BUY_SCROLL", "BUY_WAND", "BUY_VALUE")
    return (b"".join(sg.f_bool(f, 1) for f in flags)
            + sg.f_int("QTY_ITEM", len(MERCHANT_GOODS))
            + b"".join(sg.f_str("ITEM_ID", i) + sg.f_int("ITEM_COUNT", n)
                       for i, n in MERCHANT_GOODS)
            + sg.f_bool("MISSION", 0))


def mercs_tail() -> bytes:
    return (sg.f_int("QTY_UNIT", len(MERCS_UNITS))
            + b"".join(sg.f_str("UNIT_ID", u) + sg.f_int("UNIT_LEVEL", lvl)
                       + sg.f_bool("UNIT_UNIQ", 0) for u, lvl in MERCS_UNITS))


def group_body(group_id: str, units: list[str], cells: list[int]) -> bytes:
    units = units + [sg.NULL_ID] * (6 - len(units))
    return (sg.f_str("GROUP_ID", group_id)
            + b"".join(sg.f_str(f"UNIT_{k}", u) for k, u in enumerate(units))
            + b"".join(sg.f_int(f"POS_{k}", c) for k, c in enumerate(cells)))


def ruin_body(ruin_id: str, x: int, y: int, units: list[str], cells: list[int]) -> bytes:
    """`mid_ruin` из sg.ksy: голова, пустой список посетителей, отряд-охрана."""
    return (sg.f_str("RUIN_ID", ruin_id) + sg.f_str("TITLE", "Руина Ф6")
            + sg.f_str("DESC", "") + sg.f_int("IMAGE", 6)
            + sg.f_int("POS_X", x) + sg.f_int("POS_Y", y)
            + sg.f_str("CASH", RUIN_CASH) + sg.f_str("ITEM", RUIN_ITEM)
            + sg.f_str("LOOTER", sg.NULL_ID) + sg.f_int("AIPRIORITY", 3)
            + sg.f_count(ruin_id, 0)
            + group_body(ruin_id, units, cells))


# ------------------------------------------------------------- правка игрока

_PLAYER_TO_BANK = ("PLAYER_ID:s NAME_TXT:s DESC_TXT:s LORD_ID:s RACE_ID:s FOG_ID:s "
                   "KNOWN_ID:s BUILDS_ID:s FACE:i QTY_BREAKS:i")


def human_player(scen: sg.Sg) -> dict:
    """Единственный не-нейтральный игрок карты."""
    players = [sg.fields(r.body, sg.LAYOUT_PLAYER) for r in scen.find(sg.WHAT_PLAYER)]
    playable = [p for p in players if p["RACE_ID"] != sc.RACE_NEUTRAL]
    if len(playable) != 1:
        raise SystemExit("играбельных игроков %d, ожидался один" % len(playable))
    return playable[0]


def set_bank(scen: sg.Sg, bank: str) -> sg.Sg:
    """Переписать BANK игрока: поле пересобирается, хвост тела копируется байт в байт."""
    target = human_player(scen)["PLAYER_ID"]
    for rec in scen.find(sg.WHAT_PLAYER):
        if sg.fields(rec.body, "PLAYER_ID:s")["PLAYER_ID"] != target:
            continue
        c = sg.Cursor(rec.body)
        c.fields(_PLAYER_TO_BANK)
        start = c.pos
        c.str("BANK")
        return scen.with_record(rec, replace(rec, body=rec.body[:start]
                                             + sg.f_str("BANK", bank) + rec.body[c.pos:]))
    raise SystemExit("запись игрока %s не найдена" % target)


def set_list_record(scen: sg.Sg, obj_id: str, marker: str, values: list[str]) -> sg.Sg:
    """Тело `CPlayerBuildings`/`CPlayerKnownSpells` — только счётчик и список строк."""
    rec = next(r for r in scen.records if r.obj_id == obj_id)
    body = sg.f_count(obj_id, len(values)) + b"".join(sg.f_str(marker, v) for v in values)
    return scen.with_record(rec, replace(rec, body=body))


# ------------------------------------------------------------------------ план

def rebuild_plan(scen: sg.Sg) -> sg.Sg:
    """План карты заново: столица 5x5, отряды и жилы по клетке, лавки и руина по 3x3.

    `make_c4_map.rebuild_plan` объектов Ф6 не знает, поэтому здесь своя сборка. Пропустить
    объект в плане нельзя: движок ищет по плану, что лежит в клетке, и невнесённая лавка не
    заблокировала бы проход — маршрут прошёл бы сквозь неё, а прогон разошёлся бы с моделью.
    """
    entries: list[tuple[int, int, str]] = []
    cap = scen.one(sg.WHAT_CAPITAL)
    for x, y in c4.capital_tiles():
        entries.append((x, y, cap.obj_id))
    for r in scen.find(sg.WHAT_STACK):
        st = sg.parse_stack(r.body)
        entries.append((int(st["POS_X"]), int(st["POS_Y"]), r.obj_id))
    for r in scen.find(c4.WHAT_CRYSTAL):
        f = sg.fields(r.body, "CRYSTAL_ID:s RESOURCE:i POS_X:i POS_Y:i")
        entries.append((int(f["POS_X"]), int(f["POS_Y"]), r.obj_id))
    for r in scen.records:
        if r.cls == "SI":
            f = sg.fields(r.body, "SITE_ID:s IMG_ISO:i IMG_INTF:s TXT_TITLE:s TXT_DESC:s "
                                  "POS_X:i POS_Y:i")
        elif r.cls == "RU":
            f = sg.fields(r.body, "RUIN_ID:s TITLE:s DESC:s IMAGE:i POS_X:i POS_Y:i")
        else:
            continue
        for x, y in footprint((int(f["POS_X"]), int(f["POS_Y"]))):
            entries.append((x, y, r.obj_id))
    rec = scen.one(sg.WHAT_PLAN)
    size, _ = sg.parse_plan(rec.body)
    return scen.with_record(rec, replace(rec, body=sg.plan_body(rec.obj_id, size, entries)))


# ---------------------------------------------------------------------- сборка

def add_group(scen: sg.Sg, unit_ids: list[str], t: sq.Tables) -> tuple[sg.Sg, list[str], list[int]]:
    """Новые MidUnit под отряд-охрану и строй для них (крупные юниты — столбцом)."""
    new_ids = scen.next_ids("UN", len(unit_ids))
    cells = sq.place([-1] * sq.CELLS, unit_ids, 0, t)
    recs = [sg.Record(sg.WHAT_UNIT, uid, sg.unit_body(
        uid, impl.upper(), int(t.unit(impl)["LEVEL"]), int(t.unit(impl)["HIT_POINT"])))
        for uid, impl in zip(new_ids, unit_ids)]
    return scen.appended(*recs), new_ids, cells


def build(template: Path, name: str = "f6_route") -> tuple[sg.Sg, dict]:
    t = sq.Tables()
    scen = sg.Sg.read(template)
    size = len(sg.tile_grid(scen))
    scen, changed = c4.set_terrain(scen, c4.wanted_tiles(size))
    scen = c4.move_capital(scen, *c4.CAPITAL_XY)
    hero = next(r for r in scen.find(sg.WHAT_STACK)
                if sg.parse_stack(r.body)["INSIDE"] != sg.NULL_ID)
    scen = c4.move_stack(scen, hero, *c4.CAPITAL_XY)
    scen = c4.set_hero_type(scen, hero, c4.HERO_TYPE)
    scen, dropped = c4.drop_neutral_stacks(scen, keep=2)
    scen, crystals = c4.add_crystals(scen, c4.CRYSTALS)

    # Отряд игрока: пять вынужденных юнитов в стек героя.
    hero = next(r for r in scen.find(sg.WHAT_STACK) if r.obj_id == hero.obj_id)
    scen, _ = sq.add_soldiers(scen, hero, SQUAD, scen.next_ids("UN", len(SQUAD)), t)

    # Нейтралы: переставить к выходу из столицы и наполнить.
    neutral = [r for r in scen.find(sg.WHAT_STACK)
               if sg.parse_stack(r.body)["OWNER"] != sg.parse_stack(hero.body)["OWNER"]]
    if len(neutral) != 2:
        raise SystemExit("нейтральных отрядов %d, ожидалось 2" % len(neutral))
    for rec, key, soldiers in zip(neutral, ("A", "B"), (NEUTRAL_A, NEUTRAL_B)):
        scen = c4.move_stack(scen, rec, *NEUTRAL_AT[key])
        rec = next(r for r in scen.find(sg.WHAT_STACK) if r.obj_id == rec.obj_id)
        scen, _ = sq.add_soldiers(scen, rec, soldiers, scen.next_ids("UN", len(soldiers)), t)

    # Объекты: три лавки-сайта и руина с охраной.
    site_ids = scen.next_ids("SI", len(SITES))
    tails = {WHAT_MERCHANT: merchant_tail(), WHAT_MERCS: mercs_tail(), WHAT_TRAINER: b""}
    scen = scen.appended(*[
        sg.Record(what, sid, site_body(sid, img, title, "Объект прогона Ф6", x, y, tails[what]))
        for sid, (what, (x, y), img, title) in zip(site_ids, SITES)])
    scen, guard_ids, guard_cells = add_group(scen, RUIN_GUARD, t)
    ruin_id = scen.next_ids("RU")[0]
    scen = scen.appended(sg.Record(WHAT_RUIN, ruin_id,
                                   ruin_body(ruin_id, *RUIN_AT, guard_ids, guard_cells)))

    # Игрок: казна и известные заклинания. Построек не пишем — старт сцены их сотрёт.
    player = human_player(scen)
    scen = set_bank(scen, BANK)
    scen = set_list_record(scen, player["KNOWN_ID"], "SPELL_ID", KNOWN_SPELLS)

    scen = rebuild_plan(scen)
    scen = sg.set_scenario_name(scen, name)
    return scen, {"клеток перерисовано": changed, "нейтралов удалено": len(dropped),
                  "жил добавлено": len(crystals), "объектов добавлено": len(SITES) + 1,
                  "записей": len(scen.records)}


# ------------------------------------------------------------------- проверка

def target_build_cost() -> tuple[int, str]:
    """(золото, REQUIRED) целевого здания из `Gbuild.dbf`."""
    from dbf_dump import read
    row = next(r for r in read(sq.GLOBALS / "Gbuild.dbf")["rows"]
               if r["BUILD_ID"].lower() == TARGET_BUILDING.lower())
    return int(row["COST"].split(":")[0][1:]), row["REQUIRED"].lower()


def verify(path: Path) -> None:
    """Прочитать записанный файл обратно и проверить числами всё, что генератор обещал.

    Главная проверка — не «поля на месте», а «курсор дочитал тело до конца»: если новая запись
    собрана с ошибкой на байт, курсор упрётся в маркер и упадёт здесь, а не в игре.
    """
    scen = sg.Sg.read(path)          # чтение проверяет счётчик объектов и рамки всех записей
    grid = sg.tile_grid(scen)
    want = c4.wanted_tiles(len(grid))
    bad = [(x, y) for (x, y), tile in want.items() if grid[y][x] != tile[0]]
    assert not bad, f"грунт разошёлся в {len(bad)} клетках, первые {bad[:5]}"

    cap = sg.fields(scen.one(sg.WHAT_CAPITAL).body, sg.LAYOUT_CAPITAL)
    assert (cap["POS_X"], cap["POS_Y"]) == c4.CAPITAL_XY, cap

    player = human_player(scen)
    assert player["BANK"] == BANK, player["BANK"]
    gold = int(BANK.split(":")[0][1:])
    assert 0 < gold < 9999, f"казна {gold} не в (0, 9999) — потолок обрежет доход"

    builds = read_list(scen, player["BUILDS_ID"], "BUILD_ID")
    assert builds == [], f"построек в карте {builds} — их всё равно сотрёт старт сцены"
    # Цель прогона обязана быть доступна с НУЛЕВОЙ столицы и по карману: список построек
    # старт сцены обнуляет, а казну сохраняет.
    build_cost, build_req = target_build_cost()
    hire_cost = int(sq.Tables().unit(TARGET_HIRE)["ENROLL_C"].split(":")[0][1:])
    assert build_req == "g000000000", f"{TARGET_BUILDING} требует {build_req} — недоступно на старте"
    assert gold > build_cost + hire_cost, f"казна {gold} < {build_cost}+{hire_cost}"
    known = read_list(scen, player["KNOWN_ID"], "SPELL_ID")
    assert known == KNOWN_SPELLS, known

    stacks = {r.obj_id: sg.parse_stack(r.body) for r in scen.find(sg.WHAT_STACK)}
    own = [s for s in stacks.values() if s["OWNER"] == player["PLAYER_ID"]]
    assert len(own) == 1, f"своих отрядов {len(own)}, ожидался один"
    types = {r.obj_id: sg.fields(r.body, sg.LAYOUT_UNIT)["TYPE"].lower()
             for r in scen.find(sg.WHAT_UNIT)}
    squad = [types[own[0][f"UNIT_{k}"]] for k in range(1, 6)]
    assert squad == SQUAD, f"состав отряда {squad}, ожидался {SQUAD}"
    others = [s for s in stacks.values() if s["OWNER"] != player["PLAYER_ID"]]
    assert len(others) == 2, f"нейтральных отрядов {len(others)}"
    # Состав нейтрала сверяется с его СПИСКОМ (NEUTRAL_A/B), а не с константой «шесть»: после
    # 08.09.2026 отряды разной длины (3 и 4 солдата), и прежняя проверка `== 6` роняла сборку
    # на верном файле. Лидер — слот 0, солдаты — слоты 1.. по порядку списка.
    expected = {NEUTRAL_AT["A"]: NEUTRAL_A, NEUTRAL_AT["B"]: NEUTRAL_B}
    for s in others:
        pos = (int(s["POS_X"]), int(s["POS_Y"]))
        assert pos in expected, (pos, s["STACK_ID"])
        soldiers = [types[s[f"UNIT_{k}"]] for k in range(1, 6) if s[f"UNIT_{k}"] != sg.NULL_ID]
        assert soldiers == [u.lower() for u in expected[pos]], \
            f"{s['STACK_ID']} на {pos}: солдаты {soldiers}, ожидалось {expected[pos]}"

    # Тела новых записей читаются до конца: остаток — единственное честное доказательство.
    kinds = {}
    for r in scen.records:
        if r.cls == "SI":
            rest = read_site(r)
        elif r.cls == "RU":
            rest = read_ruin(r)
        else:
            continue
        assert rest == 0, f"{r.obj_id}: после разбора осталось {rest} байт"
        kinds[r.what] = kinds.get(r.what, 0) + 1
    assert kinds.get(WHAT_MERCHANT) == 1 and kinds.get(WHAT_MERCS) == 1, kinds
    assert kinds.get(WHAT_TRAINER) == 1 and kinds.get(WHAT_RUIN) == 1, kinds

    size, plan = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    ids = {r.obj_id for r in scen.records}
    dangling = sorted({e for _, _, e in plan} - ids)
    assert not dangling, f"в плане ссылки на удалённые записи: {dangling}"
    assert size == len(grid), (size, len(grid))
    occupied = {(x, y) for x, y, _ in plan}
    for corner in [c for _, c, _, _ in SITES] + [RUIN_AT]:
        missing = [t for t in footprint(corner) if t not in occupied]
        assert not missing, f"объект {corner}: клеток нет в плане {missing}"
    for r in scen.find(sg.WHAT_STACK):
        st = sg.parse_stack(r.body)
        for k in range(6):
            u = st[f"UNIT_{k}"]
            assert u == sg.NULL_ID or u in ids, f"{r.obj_id}: юнит {u} удалён, ссылка осталась"
    print("проверка чтения: грунты, столица, казна, постройки, заклинания, отряды, "
          "объекты и план сошлись")


def read_list(scen: sg.Sg, obj_id: str, marker: str) -> list[str]:
    rec = next(r for r in scen.records if r.obj_id == obj_id)
    c = sg.Cursor(rec.body)
    return [c.str(marker) for _ in range(c.count())]


def read_site(rec: sg.Record) -> int:
    """Разобрать тело сайта по классу и вернуть, сколько байт осталось непрочитанными."""
    c = sg.Cursor(rec.body)
    c.fields("SITE_ID:s IMG_ISO:i IMG_INTF:s TXT_TITLE:s TXT_DESC:s POS_X:i POS_Y:i "
             "VISITER:s AIPRIORITY:i")
    if WHAT_MERCHANT in rec.what:
        c.fields("BUY_ARMOR:b BUY_JEWEL:b BUY_WEAPON:b BUY_BANNER:b BUY_POTION:b "
                 "BUY_SCROLL:b BUY_WAND:b BUY_VALUE:b")
        for _ in range(c.int("QTY_ITEM")):
            c.str("ITEM_ID")
            c.int("ITEM_COUNT")
        c.bool("MISSION")
    elif WHAT_MERCS in rec.what:
        for _ in range(c.int("QTY_UNIT")):
            c.fields("UNIT_ID:s UNIT_LEVEL:i UNIT_UNIQ:b")
    for _ in range(c.count()):
        c.fields("SITE_ID:s VISITER:s")
    return len(rec.body) - c.pos


def read_ruin(rec: sg.Record) -> int:
    c = sg.Cursor(rec.body)
    c.fields("RUIN_ID:s TITLE:s DESC:s IMAGE:i POS_X:i POS_Y:i CASH:s ITEM:s LOOTER:s "
             "AIPRIORITY:i")
    for _ in range(c.count()):
        c.fields("RUIN_ID:s VISITER:s")
    c.fields(sg._GROUP)
    return len(rec.body) - c.pos


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--template", type=Path, default=c4.TEMPLATE)
    p.add_argument("--out", type=Path, default=OUT)
    p.add_argument("--route", type=Path, default=ROUTE_OUT)
    p.add_argument("--name", default="f6_route")
    p.add_argument("--no-route", action="store_true", help="только карта")
    a = p.parse_args(argv)
    if not a.template.exists():
        raise SystemExit(f"нет шаблона {a.template}")
    scen, stats = build(a.template, a.name)
    a.out.parent.mkdir(parents=True, exist_ok=True)
    scen.write(a.out)
    print("%s: %s" % (a.out, ", ".join(f"{k} {v}" for k, v in stats.items())))
    verify(a.out)
    if not a.no_route:
        import f6_route  # noqa: E402  — маршрут строится по уже записанной карте
        f6_route.write_route(a.out, a.route)
    return 0


if __name__ == "__main__":
    sys.exit(main())
