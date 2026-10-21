#!/usr/bin/env python3
"""Данные и сторожа девяти карт Ф7 (M4, M6, M8, M9, M10 и M11…M14) — всё, что проверяется
без файла.

    python tools/f7_plan.py              # расчёт и сторожа профилей
    python tools/f7_plan.py --selftest   # сторожа профилей на подсадной ошибке

Байты карт собирает `tools/make_f7_patch_maps.py`; там же живут сторожа, которым нужен
записанный файл. Разделение то же, что у пары `items_plan.py` / `make_items_map.py`:
арифметика и состав отдельно от разбора `.sg`.

Почему всё задаётся СЦЕНАРИЕМ, а не заплатой сохранения, и чем это доказано — в docstring
`make_f7_patch_maps.py`.

**Вторая волна (M11…M14), 09.09.2026** — четыре сценария, не закрытых двумя живыми прогонами
(`docs/superpowers/plans/2026-09-09-f7-map-economy-leaders.md` §10.11 и §11.6):

| карта | сценарий | что меряет |
|---|---|---|
| M11 `f7-m11-train` | S4 | частичное обучение у Тренера, потом полное до `XP_NEXT − 1` |
| M12 `f7-m12-capture` | S6, S10 | захват стирает гарнизон; куда девается `T_CAPTURE = 100` |
| M13 `f7-m13-thief` | S14 | треугольный бросок вора — статистикой, а не одним исходом |
| M14 `f7-m14-diplo` | S15 | шкала отношений: `D_ROD = −15`, `D_NOBLE = −5` |

Числа берутся из `.dbf`, а не выписываются руками: `GVars`, `Grace`, `Gunits`, `Gaction`,
`Lsite`. Игра отсюда не запускается.
"""
from __future__ import annotations

import argparse
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import make_c4_map as c4  # noqa: E402
import make_city_map as city  # noqa: E402  (village_tiles — пятно города 4x4)
import scenario_squads as sq  # noqa: E402
import arena_plan as ap  # noqa: E402      (Numbers: dpr/hp/effective_hp по Gunits+Gattacks)
from dbf_dump import read as dbf_read  # noqa: E402
# Геометрия, чтение `.dbf` и сторожа второй волны живут в `f7_wave2`, иначе этот файл
# перевалил бы за восемьсот строк. Имена ввозятся поимённо, чтобы `fp.X` у генератора карт
# и все ссылки внутри профилей продолжали работать без переписывания.
from f7_wave2 import (  # noqa: E402,F401
    CAP, NEAR_CAPITAL, ROD_MIN_GAP, ROD_CITY_GAP, PLOT_MIN_GAP, NEUTRAL_MIN_GAP, HP_MARGIN,
    MAP_SIZE, PLAIN_COST, SITE_TILES, RACE_EMPIRE,
    HERO_FIGHTER, HERO_MAGE, HERO_ROD, HERO_THIEF, CAT_FIGHTER, CAT_MAGE, CAT_ROD, CAT_THIEF,
    GOBLIN, SQUIRE, ARCHER, KNIGHT, TRAINER_XY, TRAINER_LSITE, CAPTURE_TIER, CAPTURE_CLEAN_GAP,
    THIEF_WEAK, THIEF_HARD, THIEF_ACTS_PER_TURN, CAT_MISFIT, CAT_POISON_STACK,
    SECOND_RACE, SECOND_LORD, SECOND_SUBRACE, SECOND_TERRAIN, SECOND_CAPITAL, RELATION_START,
    cheb, capital_gap, capital_tiles_at, site_tiles, site_entrance, plain_tiles, passable,
    gvars, grace, action_chance, bank_gold, occupied_cells,
    training_price, training_schedule, check_training, check_training_rates, check_sites,
    free_room, capture_room, check_capture,
    tri_chance, rolls_needed, thief_shown, check_thief, check_second, check_second_safety,
    print_second_wave, wall_ring, villages)
# Проценты укрепления, таблица гипотез и сторожа карты M16 — в отдельном модуле; здесь от неё
# остаётся только профиль. Зависимость односторонняя, как и у `f7_wave2`.
import f7_regen as fr  # noqa: E402
# Карта M17 (заряды талисмана, сферы и посоха): профиль, числа и сторожа — в `f7_charges`.
import f7_charges as fc  # noqa: E402

# ------------------------------------------------------------- ограничения сценария

MAX_UNIT_WANTED = 5      # потолок уровня ПОСТРОЙКИ (максимальный Gbuild.LEVEL в ванили — 5)
MAX_LEADER_WANTED = 99   # CScenarioInfo::leaderMaxLevel; при 1 прогон M8 провалится молча
MAX_SPELL_WANTED = 5     # M6 сносит жезл заклинанием 5 уровня (g000ss0119)
MAX_CITY_WANTED = 5
GOLD_CAP = 9999          # Economy.CurrencyMax, 0x586642 — казна обязана быть ниже

# -------------------------------------------------------------------- предметы

ITEM_ORB = "G000IG9041"       # Orb of Flames,     ITEM_CAT 11 -> требует L_ORB_USE
ITEM_TALISMAN = "G000IG9137"  # Talisman of Frost, ITEM_CAT 12 -> требует L_TALISMAN_USE
ITEM_BOOK = "G000IG4007"      # Книга колдовства,  ITEM_CAT 1  -> САМА даёт L_TALISMAN_USE
TALISMAN_CHARGES = 5          # GVars.TALIS_CHRG; столько же в ванильных картах

SPELL_REMOVE_ROD = "G000SS0119"   # Sinkhole, Lspell CATEGORY 10 = L_REMOVE_ROD, уровень 5

# ------------------------------------------------------------------- профили


def _n(xy: tuple[int, int], leader: str, soldiers: tuple[str, ...] = (), why: str = "",
       min_rounds: int = 1, fight: bool = True, poison: bool = False) -> dict:
    """Нейтральный отряд. `min_rounds` — сколько раундов бой обязан продлиться: применение
    предмета хоткея не имеет, и на сферу с талисманом нужно по раунду каждому.
    `fight=False` — цель, с которой драться НЕ надо (мишень вора на M10 и M13).
    `poison=True` — по этой мишени разрешено `Poison Party`: её суточная регенерация обязана
    перекрывать самое пессимистичное чтение `GVars.POISON_S` (см. `check_thief`)."""
    return {"xy": xy, "leader": leader, "soldiers": soldiers, "why": why,
            "min_rounds": min_rounds, "fight": fight, "poison": poison}


def _p(xy: tuple[int, int], leader: str, cat: int | None = None,
       units: tuple[tuple[str, int | None], ...] = (), why: str = "",
       cell: int = 0, fights: bool = False, leader_hp: int | None = None) -> dict:
    """Отряд ИГРОКА, поставленный сценарием НА КАРТУ (не в столицу — тот стек стирает лорд).

    `units` — пары (тип, HP): None полное здоровье, 0 павший, число — ранение.
    `cell` — клетка строя лидера (0 передний ряд, 1 задний).
    `fights` — этим отрядом прогон дерётся, значит к нему применим расчёт боя.
    `leader_hp` — рана САМОГО лидера; None значит полное здоровье (так у восьми карт).
    """
    return {"xy": xy, "leader": leader, "cat": cat, "units": list(units), "why": why,
            "cell": cell, "fights": fights, "leader_hp": leader_hp}


def _bag(xy: tuple[int, int], items: tuple[str, ...], why: str = "") -> dict:
    return {"xy": xy, "items": list(items), "why": why}


def _site(xy: tuple[int, int], title: str, why: str = "") -> dict:
    """Лавка 3x3. Класс записи один — Тренер; развернуть вход нечем, только выбрать место."""
    return {"xy": xy, "title": title, "why": why}


PROFILES: dict[str, dict] = {
    "m4": {
        "file": "f7_m4_temple.sg", "name": "f7-m4-temple",
        "why": "S7: Храм, лечение (в том числе частичное), воскрешение",
        "bank": "G2000:R0000:Y0000:E0000:W0000:B0000",
        # Лестница трат при казне 2000 (Gbuild.COST, Gunits.HEAL_C/REVIVE_C, перечитаны из
        # game-pristine/Globals 09.09.2026):
        #   Храм g000bb0023 = 300 -> 1700; Squire 50 HP из 100, лечение 50 x 1 = 50 -> 1650;
        #   воскрешение Ангела 600 (плоская цена REVIVE_C, не формула) -> 1050;
        #   Holy Avenger 1 HP из 250, лечение 249 x 3 = 747 -> 303;
        #   Imperial Knight 1 HP из 200, лечение 199 x 2 = 398 — денег 303, и это ровно
        #   шаг 4 сценария: ЧАСТИЧНОЕ исцеление. Скидки лорда в расчёт НЕ заложены.
        "parties": [_p(NEAR_CAPITAL, HERO_FIGHTER, CAT_FIGHTER,
                       (("g000uu0001", 50), ("g000uu0014", 0),
                        ("g000uu0155", 1), ("g000uu0003", 1)),
                       "раненые и павший: пользователь выводит стартового героя из столицы "
                       "и заводит туда этот отряд")],
        "need_dead": True,
        "bags": [], "spells": [], "neutrals": [], "village": None, "rods": {},
    },
    "m6": {
        "file": "f7_m6_rods.sg", "name": "f7-m6-rods",
        "why": "S9: сложение квот жезлов; B (отступ 3) и C (отступ 8) обязаны дать одно и то же",
        # 5 жезлов x ROD_COST 150 = 750 золота; мана — на Sinkhole (y80:e60:w110:b250).
        "bank": "G3000:R1000:Y1000:E1000:W1000:B1000",
        "parties": [_p((29, 8), HERO_ROD, CAT_ROD, why="жезлоносец площадки A"),
                    _p((29, 24), HERO_ROD, CAT_ROD, why="жезлоносец площадки B"),
                    _p((29, 40), HERO_ROD, CAT_ROD, why="жезлоносец площадки C")],
        "spells": [SPELL_REMOVE_ROD],
        "bags": [], "need_dead": False, "neutrals": [], "village": None,
        "rods": {"A": [(30, 8)],                  # контроль: один жезл, ожидается +2/сутки
                 "B": [(30, 24), (33, 24)],       # минимум по Чебышёву — ближе игра не даст
                 "C": [(30, 40), (38, 40)]},      # врозь; ответ обязан совпасть с B (+4/сутки)
    },
    "m8": {
        "file": "f7_m8_mage.sg", "name": "f7-m8-mage",
        "why": "S11/S12: гейт предметов, выбор навыка на 2 уровне, бой под сферой и талисманом",
        "bank": "G1000:R0000:Y0000:E0000:W0000:B0000",
        # Лидер в ЗАДНЕМ ряду, и это отступление от буквы ТЗ, названное вслух. У Архимага 65 HP
        # и площадная атака (`Gattacks.REACH = 1`, L_ALL): из заднего ряда он бьёт КАЖДЫЙ раунд
        # по всему чужому строю, а ближний бой врага туда не достаёт.
        # Два Ангела (225 HP, 125 урона) вместо гвардейцев — расчёт, а не вкус: опыт делится
        # поровну между живыми, значит герою нужен нейтрал на 3 x 150 = 450 опыта, а такой
        # нейтрал бьёт больно.
        "parties": [_p(NEAR_CAPITAL, HERO_MAGE, CAT_MAGE,
                       (("g000uu0014", None), ("g000uu0014", None)),
                       "герой-МАГ прогона: стартовый герой зависит от лорда и для S11 не годится",
                       cell=1, fights=True)],
        # Вещи — МЕШКАМИ на карте, а не в инвентаре: инвентарь есть только у отряда, а подбор
        # мешка вдобавок сам по себе непроверенная механика.
        "bags": [_bag((10, 9), (ITEM_ORB,), "сфера: требует L_ORB_USE, он у Архимага врождён"),
                 _bag((10, 8), (ITEM_TALISMAN,), "талисман: L_TALISMAN_USE Архимагу НЕ дан"),
                 _bag((10, 7), (ITEM_BOOK,), "Книга колдовства: она и открывает талисман")],
        "need_items": [ITEM_ORB, ITEM_TALISMAN, ITEM_BOOK],
        "need_dead": False, "spells": [], "village": None, "rods": {},
        "neutrals": [
            _n((14, 7), "g000uu6015", ("g000uu5236",),
               "S11 шаг 3: Король гномов и гном — 585 опыта на троих живых, герою 195 при "
               "нужных 150"),
            _n((14, 13), "g000uu5129", (),
               "S12: Морской змей — бой на два раунда, сфера в первом, талисман во втором",
               min_rounds=2),
        ],
        "xp_battle": 0, "xp_need": 150,          # Gunits.XP_NEXT Архимага = 150
    },
    "m9": {
        "file": "f7_m9_xpstop.sg", "name": "f7-m9-xpstop",
        "why": "S13: «Остановить продвижение» (L) — куда девается опыт остановленного юнита",
        "bank": "G1000:R0000:Y0000:E0000:W0000:B0000",
        # Три обычных юнита РАЗНОГО уровня: Gunits.LEVEL 1, 2 и 3.
        "parties": [_p(NEAR_CAPITAL, HERO_FIGHTER, CAT_FIGHTER,
                       (("g000uu0001", None), ("g000uu0002", None), ("g000uu0003", None)),
                       "три юнита разного уровня — на них и жмут L", cell=1, fights=True)],
        "bags": [], "spells": [], "need_dead": False, "village": None, "rods": {},
        # 90 опыта Волка на четверых живых — по 22; если остановленный из дележа выпадает,
        # остальным достанется по 30. Разница видна сразу, это и есть измерение.
        "neutrals": [_n((15, 5), "g000uu5139", (), "бой 1: Волк, 90 опыта"),
                     _n((15, 9), "g000uu5139", (), "бой 2"),
                     _n((15, 13), "g000uu5139", (), "запас"),
                     _n((15, 17), "g000uu5139", (), "запас")],
    },
    "m10": {
        "file": "f7_m10_thief.sg", "name": "f7-m10-thief",
        "why": "S14/S15: действия вора, шкала дипломатии, штраф -15 за жезл",
        "bank": "G5000:R1000:Y1000:E1000:W1000:B1000",
        # У вора лидерство 1, и опыта он не получает вовсе (категория Noble исключена из
        # дележа, `unitutils.cpp:661`). Солдат ему не даём: они бы только гибли.
        # Вор жезлов не ставит: у `L_NOBLE` в `GLabi.dbf` нет ни одной строки, поэтому для
        # S15 рядом стоит отдельный жезлоносец.
        "parties": [_p(NEAR_CAPITAL, HERO_THIEF, CAT_THIEF, why="вор прогона"),
                    _p((11, 3), HERO_ROD, CAT_ROD, why="жезлоносец под штраф -15 (S15)")],
        "bags": [], "spells": [], "need_dead": False,
        "village": {"xy": (16, 6), "tier": 1, "name": "Чужой город"},
        "rods": {"S15": [(12, 3)]},
        # Оба отряда — МИШЕНИ ВОРА, а не противники: драться с ними нельзя (вор один и слаб).
        # Разница NEGOTIATE 10 против 70 и есть измерение.
        "neutrals": [
            _n((12, 12), "g000uu5130", (), "слабый вожак: NEGOTIATE 10 — низкое сопротивление",
               fight=False),
            _n((12, 16), "g000uu5119", (), "Красный дракон: NEGOTIATE 70 — другой конец шкалы",
               fight=False),
        ],
    },
    "m11": {
        "file": "f7_m11_train.sg", "name": "f7-m11-train",
        "why": "S4: обучение сперва ЧАСТИЧНОЕ, потом полное — опыт обязан встать на XP_NEXT - 1",
        # Казна 5 золота. Дневной тик срабатывает уже при загрузке карты, значит на первом ходу
        # в кошельке 5 + 100 = 105, а полное обучение Squire стоит 4 x 79 = 316: денег заведомо
        # на треть. Расписание считает `training_schedule`, оно же печатается в плане.
        "bank": "G0005:R0000:Y0000:E0000:W0000:B0000",
        "parties": [_p((15, 7), HERO_FIGHTER, CAT_FIGHTER,
                       ((SQUIRE, None), (ARCHER, None), (KNIGHT, None)),
                       "отряд прогона в двух клетках от входа Тренера (13,5); все трое солдат "
                       "поставлены сценарием, значит опыт у них ровно 0")],
        "sites": [_site(TRAINER_XY, "Тренер", "Lsite id 6 — настоящий Тренер, строки 3 мертвы")],
        # (кого учить, до конца ли). Первый шаг обязан выйти частичным, последний — точным.
        # Четвёртым шагом стоит Knight: у него `TRAINING_C` = 5 против 4 у Squire и Archer, и
        # только этот шаг различает «ставка из Gunits» и «всегда 4» (сторож check_training_rates).
        "train": [(SQUIRE, "partial"), (ARCHER, "full"), (SQUIRE, "full"), (KNIGHT, "partial")],
        "turns": 8,
        "bags": [], "spells": [], "neutrals": [], "village": None, "rods": {},
        "need_dead": False,
    },
    "m12": {
        "file": "f7_m12_capture.sg", "name": "f7-m12-capture",
        "why": "S6/S10: захват стирает гарнизон; T_CAPTURE = 100 на просторе и вдали от столицы",
        "bank": "G1000:R0000:Y0000:E0000:W0000:B0000",
        # Отряд прогона стоит У ГОРОДА, а не у столицы: столица нарочно далеко, и идти к городу
        # три хода было бы чистой тратой времени пользователя.
        "parties": [_p((35, 35), HERO_FIGHTER, CAT_FIGHTER,
                       ((SQUIRE, None), (SQUIRE, None), (SQUIRE, None)),
                       "отряд захвата в двух клетках от входа города (33,33)", fights=True)],
        "village": {"xy": (30, 30), "tier": CAPTURE_TIER, "name": "Гарнизонный город",
                    "garrison": (GOBLIN,) * 3, "fight": True, "owner": "neutral"},
        "turns": 6,
        "bags": [], "spells": [], "neutrals": [], "rods": {}, "need_dead": False,
    },
    "m13": {
        "file": "f7_m13_thief.sg", "name": "f7-m13-thief",
        "why": "S14: треугольный бросок вора — статистикой на двух порогах, 80 % и 20 %",
        "bank": "G2000:R0000:Y0000:E0000:W0000:B0000",
        # Десять воров: по пяти на мишень. Допущение THIEF_ACTS_PER_TURN = 1 даёт 5 попыток на
        # мишень за ход, за 10 ходов 50 — больше расчётных `rolls_needed()`.
        "parties": [_p((14, y), HERO_THIEF, CAT_THIEF, why="вор мишени 80 %")
                    for y in range(8, 13)]
                   + [_p((14, y), HERO_THIEF, CAT_THIEF, why="вор мишени 20 %")
                      for y in range(18, 23)],
        "neutrals": [
            _n((16, 10), THIEF_WEAK, (GOBLIN, GOBLIN),
               "мишень 80 %: NEGOTIATE 10. Отравление ей НЕ по силам (регенерация +5 в день), "
               "поэтому по ней бьют Counterfeit Orders — тот же CHANCE 90, но без урона",
               fight=False),
            _n((16, 20), THIEF_HARD, (GOBLIN, GOBLIN),
               "мишень 20 %: Красный дракон, NEGOTIATE 70. 800 HP и REGEN 40 -> +320 в день, "
               "поэтому по нему разрешено и Poison Party", fight=False, poison=True),
        ],
        "turns": 10, "thief_stat": True,
        "bags": [], "spells": [], "village": None, "rods": {}, "need_dead": False,
    },
    "m14": {
        "file": "f7_m14_diplo.sg", "name": "f7-m14-diplo",
        "why": "S15: шкала отношений — D_ROD -15 за жезл и D_NOBLE -5 за действие вора",
        "bank": "G3000:R1000:Y1000:E1000:W1000:B1000",
        "parties": [_p((12, 8), HERO_ROD, CAT_ROD,
                       why="жезлоносец: жезл ставится в (13,8), у самой чужой деревни"),
                    _p((13, 3), HERO_THIEF, CAT_THIEF,
                       why="вор: до входа чужой деревни (19,7) шесть клеток, 12 очков из 25")],
        "village": {"xy": (16, 4), "tier": 1, "name": "Клановая деревня",
                    "garrison": (), "fight": False, "owner": "second"},
        "second": {"race": SECOND_RACE, "lord": SECOND_LORD, "subrace": SECOND_SUBRACE,
                   "terrain": SECOND_TERRAIN, "capital": SECOND_CAPITAL,
                   "name": "Горные кланы", "relation": RELATION_START,
                   "bank": "G0000:R0000:Y0000:E0000:W0000:B0000"},
        "rods": {"S15": [(13, 8)]},
        "noble_acts": 2,          # столько действий вора закладывает памятка
        "turns": 2,
        "bags": [], "spells": [], "neutrals": [], "need_dead": False,
    },
    "m16": {
        "file": "f7_m16_regen.sg", "name": "f7-m16-regen",
        "why": "S8: дневной тик ВНУТРИ укрепления — REGEN_F складывается с собственной "
               "регенерацией юнита или заменяет её",
        # Казна на всё про всё: тратить её прогон не собирается, лечение сорвало бы замер.
        "bank": "G1000:R0000:Y0000:E0000:W0000:B0000",
        # Шесть одинаковых отрядов, по одному у каждого укрепления, каждый в шаге от входа.
        # Рана в 1 HP и лидеру, и солдату: глубже некуда, а ноль — это павший, он не лечится.
        # Две точки на укрепление: HIT_POINT 150 у лидера и 100 у Squire дают разные
        # округления одного и того же процента.
        "parties": [_p(fr.party_xy(f), fr.REGEN_LEADER, CAT_FIGHTER,
                       ((SQUIRE, fr.REGEN_WOUND),), "замер у %s (вход %s)"
                       % (f["title"], fr.entrance(f)), leader_hp=fr.REGEN_WOUND)
                    for f in fr.FORTS],
        # Гарнизоны пусты: пользователь входит и захватывает БЕЗ боя — бой сбил бы здоровье.
        "villages": [{"xy": f["xy"], "tier": f["tier"], "name": "Город %d уровня" % f["tier"],
                      "garrison": (), "fight": False, "owner": "neutral"}
                     for f in fr.cities()],
        "turns": 2,             # ход первый — завести отряды, ход второй — дневной тик внутри
        "village": None, "bags": [], "spells": [], "neutrals": [], "rods": {},
        "need_dead": False,
    },
    # НЕ в ORDER: собирает `make_f7_m17_map.py` — лидеру нужен модификатор, общий генератор
    # его не пишет и перезаписал бы карту без него.
    "m17": fc.PROFILE,
}
ORDER = ["m4", "m6", "m8", "m9", "m10", "m11", "m12", "m13", "m14", "m16"]


def profile(key: str) -> dict:
    if key not in PROFILES:
        raise SystemExit("нет карты %s, есть %s" % (key, ", ".join(ORDER)))
    return PROFILES[key]


def fighting_party(prof: dict) -> dict | None:
    return next((p for p in prof["parties"] if p["fights"]), None)

# ------------------------------------------------------------------- сторожа
#
# Каждый сторож роняет сборку, и каждый проверяется подсадной ошибкой в `--selftest`:
# «упало бы» надо доказать, а не пообещать.

def check_scenario_limits(info: dict, key: str) -> None:
    """Потолки сценария: `MAX_UNIT` — уровня ПОСТРОЙКИ, `MAX_LEADER` — уровня героя."""
    if int(info["MAX_UNIT"]) != MAX_UNIT_WANTED:
        raise SystemExit("%s: MAX_UNIT %s, нужно %d — иначе часть зданий не построить "
                         "(ExceedsMaxLevel)" % (key, info["MAX_UNIT"], MAX_UNIT_WANTED))
    if int(info["MAX_LEADER"]) < MAX_LEADER_WANTED:
        raise SystemExit("%s: MAX_LEADER %s, нужно %d — это leaderMaxLevel (0x5E3A26): при "
                         "маленьком значении герой молча не растёт в уровне"
                         % (key, info["MAX_LEADER"], MAX_LEADER_WANTED))
    if int(info["MAX_SPELL"]) < MAX_SPELL_WANTED:
        raise SystemExit("%s: MAX_SPELL %s, нужно %d" % (key, info["MAX_SPELL"], MAX_SPELL_WANTED))
    if int(info["MAX_CITY"]) != MAX_CITY_WANTED:
        raise SystemExit("%s: MAX_CITY %s, нужно %d" % (key, info["MAX_CITY"], MAX_CITY_WANTED))


def check_bank(prof: dict, key: str) -> None:
    for part in prof["bank"].split(":"):
        if int(part[1:]) > GOLD_CAP:
            raise SystemExit("%s: казна %s выше потолка %d — доход начнёт обрезаться"
                             % (key, prof["bank"], GOLD_CAP))


def check_parties(t: sq.Tables, prof: dict, key: str, parties: list[dict] | None = None) -> None:
    """Отряды игрока: они есть, лидер того класса, лидерства хватает, павший на месте.

    Класс — по `Gunits.LEADER_CAT`, а не по имени: `L_NOBLE` это вор, не дворянин.
    """
    parties = prof["parties"] if parties is None else parties
    if not parties:
        raise SystemExit("%s: профиль не задаёт ни одного отряда игрока — стартовый отряд "
                         "пересоздаёт лорд, задавать состав больше нечем" % key)
    for p in parties:
        raw = str(t.unit(p["leader"])["LEADER_CAT"]).strip()
        got = int(raw) if raw and raw != "None" else 0
        if p["cat"] is not None and got != p["cat"]:
            raise SystemExit("%s: лидер %s (%s) в %s имеет LEADER_CAT %d, нужен %d — прогон "
                             "измеряет именно этот класс"
                             % (key, p["leader"], t.name(p["leader"]), p["xy"], got, p["cat"]))
        lead = int(t.unit(p["leader"])["LEADERSHIP"] or 0)
        if len(p["units"]) > lead:
            raise SystemExit("%s: солдат %d при лидерстве %d в отряде %s — движок урежет отряд"
                             % (key, len(p["units"]), lead, p["xy"]))
    if prof["need_dead"] and not any(hp == 0 for p in parties for _, hp in p["units"]):
        raise SystemExit("%s: в отрядах нет ПАВШЕГО юнита (HP = 0) — воскрешать будет некого"
                         % key)


def check_bags(prof: dict, key: str, bags: list[dict] | None = None) -> None:
    """Нужные вещи обязаны лежать в мешках и существовать в `GItem.DBF`."""
    bags = prof["bags"] if bags is None else bags
    known = {r["ITEM_ID"].upper() for r in dbf_read(sq.GLOBALS / "GItem.DBF")["rows"]}
    have = {i.upper() for b in bags for i in b["items"]}
    missing = [i for i in prof.get("need_items", []) if i.upper() not in have]
    if missing:
        raise SystemExit("%s: ни в одном мешке нет %s — без них гейт предметов не проверить"
                         % (key, ", ".join(missing)))
    unknown = [i for b in bags for i in b["items"] if i.upper() not in known]
    if unknown:
        raise SystemExit("%s: предметов %s нет в GItem.DBF" % (key, ", ".join(unknown)))
    empty = [b["xy"] for b in bags if not b["items"]]
    if empty:
        raise SystemExit("%s: пустые мешки в %s — движок покажет их и не даст ничего"
                         % (key, empty))


def check_spells(prof: dict, key: str, spells: list[str] | None = None) -> None:
    """Заклинания игрока: существуют и по уровню проходят под потолок сценария."""
    spells = prof["spells"] if spells is None else spells
    rows = {r["SPELL_ID"].upper(): r for r in dbf_read(sq.GLOBALS / "Gspells.dbf")["rows"]}
    for s in spells:
        row = rows.get(s.upper())
        if row is None:
            raise SystemExit("%s: заклинания %s нет в Gspells.dbf" % (key, s))
        if int(row["LEVEL"]) > MAX_SPELL_WANTED:
            raise SystemExit("%s: заклинание %s уровня %s выше потолка сценария MAX_SPELL %d"
                             % (key, s, row["LEVEL"], MAX_SPELL_WANTED))


def check_rod_gaps(prof: dict, key: str, plots: dict | None = None) -> None:
    """Правила постановки жезлов, взятые из движка, а не из головы."""
    plots = prof["rods"] if plots is None else plots
    towns = list(c4.capital_tiles())
    for v in villages(prof):
        towns += city.village_tiles(v["xy"])
    if prof.get("second"):
        towns += capital_tiles_at(prof["second"]["capital"])
    for plot, cells in plots.items():
        for i, a in enumerate(cells):
            town = min(cheb(a, t) for t in towns)
            if town < ROD_CITY_GAP:
                raise SystemExit("%s: жезл %s площадки %s в %d клетках от города — движок "
                                 "запрещает коробку 3x3 вокруг города (0x6137FE)"
                                 % (key, a, plot, town))
            for b in cells[i + 1:]:
                if cheb(a, b) < ROD_MIN_GAP:
                    raise SystemExit("%s: жезлы %s и %s площадки %s ближе %d по Чебышёву — "
                                     "серверная проверка 0x5D9BDB откажет в постановке"
                                     % (key, a, b, plot, ROD_MIN_GAP))
    flat = [(p, c) for p, cells in plots.items() for c in cells]
    for i, (pa, a) in enumerate(flat):
        for pb, b in flat[i + 1:]:
            if pa != pb and cheb(a, b) < PLOT_MIN_GAP:
                raise SystemExit("%s: площадки %s и %s сблизились на %d — их суточный прирост "
                                 "сольётся и раздельно не посчитается" % (key, pa, pb, cheb(a, b)))


def check_spacing(prof: dict, key: str) -> None:
    """Всё на карте, ничто не наложено друг на друга, столица и клетки жезлов свободны."""
    seen: dict[tuple[int, int], str] = {}
    capital = set(c4.capital_tiles())
    rods = {c for cells in prof["rods"].values() for c in cells}
    for xy, what in occupied_cells(prof):
        if not (0 <= xy[0] < 48 and 0 <= xy[1] < 48):
            raise SystemExit("%s: клетка %s (%s) вне карты 48x48" % (key, xy, what))
        if xy in capital:
            raise SystemExit("%s: %s в %s стоит на пятне столицы" % (key, what, xy))
        if xy in rods:
            raise SystemExit("%s: %s в %s занял клетку жезла — движок откажет в постановке"
                             % (key, what, xy))
        if xy in seen:
            raise SystemExit("%s: в клетке %s и %s, и %s" % (key, xy, seen[xy], what))
        seen[xy] = what
    foes = [tuple(n["xy"]) for n in prof["neutrals"]]
    for i, a in enumerate(foes):
        if capital_gap(a) < NEUTRAL_MIN_GAP:
            raise SystemExit("%s: нейтрал %s в %d клетках от столицы" % (key, a, capital_gap(a)))
        for b in foes[i + 1:]:
            if cheb(a, b) < NEUTRAL_MIN_GAP:
                raise SystemExit("%s: нейтралы %s и %s ближе %d — второй бой начнётся вне "
                                 "очереди" % (key, a, b, NEUTRAL_MIN_GAP))


# --------------------------------------------------------------- расчёт боёв

def battle_targets(prof: dict) -> list[dict]:
    """С кем прогон дерётся: нейтральные отряды плюс гарнизон города, если его берут боем.

    Защитникам внутри укрепления движок кладёт `GcityInf.PROTECTION` — плоскую прибавку к
    броне (`PROT_n`, ранг 1 по хуку `armor` первого прогона), поэтому у гарнизона она входит
    в расчёт раундов, а у нейтралов в поле — нет.
    """
    out = list(prof["neutrals"])
    v = prof.get("village")
    if v and v.get("fight") and v.get("garrison"):
        out.append(dict(_n(tuple(v["xy"]), v["garrison"][0], tuple(v["garrison"][1:]),
                           "гарнизон города уровня %d" % int(v["tier"])),
                        bonus_armor=int(gvars()["PROT_%d" % int(v["tier"])])))
    return out


def battle_rows(t: sq.Tables, prof: dict) -> list[dict]:
    """Раунды, потери и опыт по каждому бою. Модель грубая и консервативная в опасную сторону.

    Наш урон: ближники — только те, что помещаются в передний ряд (три клетки), по одной цели;
    неближний лидер бьёт откуда стоит, а площадная атака (`REACH 1`) умножается на число врагов.
    Врагу разрешено бить только передним рядом — задний вступает, когда передний вымер; это
    единственное послабление модели, и оно компенсировано запасом `HP_MARGIN`.

    Опыт: `XP_KILLED` каждого убитого делится поровну между живыми, поэтому герою достаётся
    сумма, делённая на размер отряда.
    """
    party = fighting_party(prof)
    if party is None:
        return []
    n = ap.Numbers(t)
    squad = [party["leader"]] + [u for u, _ in party["units"]]
    melee = [u for u in squad if t.is_melee(u)]
    ranged = [u for u in squad if not t.is_melee(u)]
    front_hp = sum(n.hp(u) for u in melee[:3]) or n.hp(party["leader"])
    rows = []
    for k, b in enumerate(battle_targets(prof)):
        if not b["fight"]:                   # мишень вора: драться с ней прогон не собирается
            continue
        foes = [b["leader"]] + list(b["soldiers"])
        bonus = b.get("bonus_armor", 0)      # прибавка укрепления защитникам (PROT_n)
        our = sum(n.dpr(u) for u in melee[:3]) + sum(n.dpr(u, targets=len(foes)) for u in ranged)
        if our <= 0:
            raise SystemExit("%s бой %d: отряд не наносит урона — бой не кончится"
                             % (prof["name"], k))
        foe_hp = sum(n.hp(u) / (1.0 - min(n.armor(u) + bonus, 90) / 100.0) for u in foes)
        rounds = math.ceil(foe_hp / our)
        their = sum(n.dpr(u) for u in foes[:3])
        xp_total = sum(int(t.unit(u)["XP_KILLED"] or 0) for u in foes)
        rows.append({"бой": k, "врагов": len(foes), "лидер": t.name(b["leader"]),
                     "HP врага": round(foe_hp),
                     "наш урон/раунд": round(our), "их урон/раунд": round(their),
                     "раундов": rounds, "потери": round(their * rounds),
                     "HP переднего ряда": round(front_hp),
                     "нужно раундов": int(b["min_rounds"]),
                     "опыт всего": xp_total, "опыт герою": xp_total // len(squad)})
    return rows


def check_battles(t: sq.Tables, prof: dict, key: str, rows: list[dict] | None = None) -> None:
    rows = battle_rows(t, prof) if rows is None else rows
    for r in rows:
        if r["потери"] > r["HP переднего ряда"] * (1.0 - HP_MARGIN):
            raise SystemExit("%s бой %d (%s): расчётные потери %d при переднем ряде %d — "
                             "нейтрал слишком силён, отряд ляжет и прогон оборвётся"
                             % (key, r["бой"], r["лидер"], r["потери"], r["HP переднего ряда"]))
        if r["раундов"] < r["нужно раундов"]:
            raise SystemExit("%s бой %d (%s): бой длится %d раундов при нужных %d — "
                             "пользователь не успеет применить предметы"
                             % (key, r["бой"], r["лидер"], r["раундов"], r["нужно раундов"]))
    if prof.get("xp_need"):
        r = rows[prof["xp_battle"]]
        if r["опыт герою"] < prof["xp_need"]:
            raise SystemExit("%s бой %d: герою достанется %d опыта при нужных %d — опыт делится "
                             "поровну между живыми, нейтрал слишком дёшев"
                             % (key, r["бой"], r["опыт герою"], prof["xp_need"]))


def check_profile(t: sq.Tables, key: str, prof: dict) -> list[dict]:
    """Всё, что проверяемо без файла. Возвращает расчёт боёв, чтобы его напечатали."""
    check_bank(prof, key)
    check_parties(t, prof, key)
    check_bags(prof, key)
    check_spells(prof, key)
    check_rod_gaps(prof, key)
    check_spacing(prof, key)
    check_sites(t, prof, key)
    check_training(t, prof, key)
    check_training_rates(t, prof, key)
    check_capture(t, prof, key)
    check_thief(t, prof, key)
    check_second(t, prof, key)
    fr.check_regen(t, prof, key)
    rows = battle_rows(t, prof)
    check_battles(t, prof, key, rows)
    return rows

# ------------------------------------------------------------------- печать

def print_plan(t: sq.Tables) -> None:
    for key in ORDER:
        prof = profile(key)
        rows = check_profile(t, key, prof)
        print("%s -> %-14s %s" % (key, prof["name"], prof["why"]))
        print("   казна %s" % prof["bank"])
        for p in prof["parties"]:
            print("   отряд %s: %s (LEADER_CAT %s, HP %s)%s — %s"
                  % (tuple(p["xy"]), t.name(p["leader"]), p["cat"],
                     "полное" if p.get("leader_hp") is None else p["leader_hp"],
                     "".join(", %s HP %s" % (t.name(u), "полное" if hp is None else hp)
                             for u, hp in p["units"]), p["why"]))
        for b in prof["bags"]:
            print("   мешок %s: %s — %s" % (tuple(b["xy"]), ", ".join(b["items"]), b["why"]))
        print_second_wave(t, prof)
        fr.print_regen(t, prof)
        if prof["spells"]:
            print("   заклинания игроку: %s" % ", ".join(prof["spells"]))
        for plot, cells in prof["rods"].items():
            gaps = [cheb(cells[i], cells[i + 1]) for i in range(len(cells) - 1)]
            print("   площадка %s: %s, отступ между жезлами %s, до столицы %d"
                  % (plot, cells, gaps or "—", min(capital_gap(c) for c in cells)))
        for r in rows:
            print("   бой %d %-16s врагов %d, HP %5d, раундов %d из нужных %d, потери %4d из "
                  "%4d, опыт %4d -> герою %3d"
                  % (r["бой"], r["лидер"], r["врагов"], r["HP врага"], r["раундов"],
                     r["нужно раундов"], r["потери"], r["HP переднего ряда"],
                     r["опыт всего"], r["опыт герою"]))
    print("сторожа профилей пройдены на всех %d картах" % len(ORDER))

from f7_cli import raises  # noqa: E402,F401  (общий каркас генераторов Ф7; старые скрипты зовут fp.raises)

def profile_cases(t: sq.Tables) -> list[tuple[str, str | None]]:
    """Сторожа профиля: порча данных, а не файла."""
    m4, m6, m8, m10 = (profile(k) for k in ("m4", "m6", "m8", "m10"))
    ok = {"MAX_UNIT": 5, "MAX_SPELL": 5, "MAX_LEADER": 99, "MAX_CITY": 5}
    mage, thief, roder = fighting_party(m8), m10["parties"][0], m6["parties"][0]
    return [
        ("MAX_UNIT не 5", raises(
            lambda: check_scenario_limits(dict(ok, MAX_UNIT=2), "x"))),
        ("потолок уровня героя равен 1", raises(
            lambda: check_scenario_limits(dict(ok, MAX_LEADER=1), "x"))),
        ("MAX_SPELL ниже нужного", raises(
            lambda: check_scenario_limits(dict(ok, MAX_SPELL=2), "x"))),
        ("профиль без отряда игрока", raises(lambda: check_parties(t, m4, "m4", []))),
        ("герой не маг", raises(lambda: check_parties(
            t, m8, "m8", [dict(mage, leader=HERO_FIGHTER)]))),
        ("герой не вор", raises(lambda: check_parties(
            t, m10, "m10", [dict(thief, leader=HERO_FIGHTER)]))),
        ("герой не жезлоносец", raises(lambda: check_parties(
            t, m6, "m6", [dict(roder, leader=HERO_MAGE)]))),
        ("солдат больше лидерства", raises(lambda: check_parties(
            t, m10, "m10", [dict(thief, units=[("g000uu0001", None)] * 3)]))),
        ("в отряде нет павшего юнита", raises(lambda: check_parties(
            t, m4, "m4", [dict(m4["parties"][0],
                               units=[(u, None) for u, _ in m4["parties"][0]["units"]])]))),
        ("в мешках нет Книги колдовства", raises(
            lambda: check_bags(m8, "m8", [_bag((10, 9), (ITEM_ORB, ITEM_TALISMAN))]))),
        ("в мешках нет сферы", raises(
            lambda: check_bags(m8, "m8", [_bag((10, 9), (ITEM_TALISMAN, ITEM_BOOK))]))),
        ("в мешке несуществующий предмет", raises(
            lambda: check_bags(m8, "m8", m8["bags"] + [_bag((11, 9), ("G000IG9999",))]))),
        ("мешок пуст", raises(
            lambda: check_bags(dict(m8, need_items=[]), "m8", [_bag((11, 9), ())]))),
        ("заклинания нет в Gspells", raises(
            lambda: check_spells(m6, "m6", ["G000SS9999"]))),
        ("жезлы ближе трёх клеток", raises(
            lambda: check_rod_gaps(m6, "m6", {"B": [(30, 24), (32, 24)]}))),
        ("жезл вплотную к столице", raises(
            lambda: check_rod_gaps(m6, "m6", {"A": [(10, 10)]}))),
        ("площадки жезлов слились", raises(
            lambda: check_rod_gaps(m6, "m6", {"A": [(30, 8)], "B": [(30, 12), (33, 12)]}))),
        ("нейтралы стоят вплотную", raises(
            lambda: check_spacing(dict(m8, neutrals=[_n((14, 7), "g000uu6015"),
                                                     _n((14, 8), "g000uu6006")]), "m8"))),
        ("мешок лёг на отряд игрока", raises(
            lambda: check_spacing(dict(m8, bags=[_bag(NEAR_CAPITAL, (ITEM_ORB,))]), "m8"))),
        ("отряд игрока встал на клетку жезла", raises(
            lambda: check_spacing(dict(m6, parties=[_p((30, 8), HERO_ROD, CAT_ROD)]), "m6"))),
        ("нейтрал слишком силён по расчёту", raises(
            lambda: check_battles(t, dict(m8, neutrals=[_n((14, 7), "g000uu6016")]), "m8"))),
        ("нейтрал не даёт нужного опыта", raises(
            lambda: check_battles(t, dict(m8, neutrals=[_n((14, 7), "g000uu5130")]), "m8"))),
        ("казна выше потолка 9999", raises(
            lambda: check_bank(dict(m8, bank="G99999:R0000:Y0000:E0000:W0000:B0000"), "m8"))),
    ] + second_wave_cases(t) + fr.regen_cases(t, profile("m16"), raises)


def second_wave_cases(t: sq.Tables) -> list[tuple[str, str | None]]:
    """Сторожа карт M11…M14: порча ровно одного обещания за раз."""
    m11, m12, m13, m14 = (profile(k) for k in ("m11", "m12", "m13", "m14"))
    rich = dict(m11, bank="G9000:R0000:Y0000:E0000:W0000:B0000")
    # g000uu5012 — нейтрал с ARMOR 20: подсадной гарнизон, у которого «Защита» не нулевая
    armored = dict(m12, village=dict(m12["village"], garrison=("g000uu5012",) * 3))
    tight = dict(m12, walls=wall_ring(m12["village"]["xy"], span=3))
    near = dict(m12, village=dict(m12["village"], xy=(12, 6)))
    few = dict(m13, parties=m13["parties"][:2])
    swap = dict(m13, neutrals=[dict(n, poison=not n["poison"]) for n in m13["neutrals"]])
    return [
        ("обучение проходит целиком", raises(lambda: check_training(t, rich, "m11"))),
        ("Тренер прижат к отряду вплотную", raises(lambda: check_sites(
            t, dict(m11, sites=[_site((13, 6), "Тренер")]), "m11"))),
        ("у гарнизона ненулевая броня", raises(lambda: check_capture(t, armored, "m12"))),
        ("вокруг города меньше 100 клеток", raises(lambda: check_capture(t, tight, "m12"))),
        ("город у самой столицы (случай M3)", raises(lambda: check_capture(t, near, "m12"))),
        ("воров слишком мало для статистики", raises(lambda: check_thief(t, few, "m13"))),
        ("яд разрешён по хрупкой мишени", raises(lambda: check_thief(t, swap, "m13"))),
        ("второго игрока нет", raises(lambda: check_second(t, dict(m14, second=None), "m14"))),
        ("сосед той же расы", raises(lambda: check_second(
            t, dict(m14, second=dict(m14["second"], race=RACE_EMPIRE)), "m14"))),
        ("сосед начинает с войны", raises(lambda: check_second(
            t, dict(m14, second=dict(m14["second"], relation=10)), "m14"))),
        ("чужая столица под боком", raises(lambda: check_second(
            t, dict(m14, second=dict(m14["second"], capital=(20, 20))), "m14"))),
        ("у соседа есть казна на найм", raises(lambda: check_second(
            t, dict(m14, second=dict(m14["second"],
                                     bank="G5000:R0000:Y0000:E0000:W0000:B0000")), "m14"))),
    ]


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--selftest", action="store_true", help="сторожа профилей подсадной ошибкой")
    a = p.parse_args(argv)
    t = sq.Tables()
    if not a.selftest:
        print_plan(t)
        return 0
    cases = profile_cases(t)
    if not cases:
        raise SystemExit("selftest не сделал ни одного сравнения — это не проверка")
    bad = 0
    for name, msg in cases:
        print("%-46s %s" % (name, ("ловится: " + msg.splitlines()[0]) if msg else "ПРОПУЩЕН"))
        bad += msg is None
    print("профили: сработали %d сторожей из %d сравнений" % (len(cases) - bad, len(cases)))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
