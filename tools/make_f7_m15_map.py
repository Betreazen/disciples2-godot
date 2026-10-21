#!/usr/bin/env python3
"""Карта M15 «дипломатия» — заплата поверх карты, собранной ПОЛЬЗОВАТЕЛЕМ в ScenEdit.

    python tools/make_f7_m15_map.py             # записать game-dev/Exports/f7_m15_diplo.sg
    python tools/make_f7_m15_map.py --plan      # только числа и сторожа, ничего не писать
    python tools/make_f7_m15_map.py --selftest  # порча плюс сторож, включая ЭТАЛОН

**Почему не свой генератор.** Второго играбельного игрока мы писали сами
(`tools/sg_second_player.py`), и на живом прогоне это роняло игру. Пользователь собрал карту
`game-dev/Exports-archive/2-capital-test.sg` штатным редактором: 121 запись, две столицы с непустыми
гарнизонами, три пары дипломатии. Этот файл — ЭТАЛОН, он открывается только на чтение;
результат пишется отдельным именем `f7_m15_diplo.sg`. Всё, чего в эталоне не хватает для S15,
дописывается здесь и проверяется чтением записанного файла.

**Как упакован `RELATION`** (`DiplomacyRelation.relation`, u32; разбор
`reference/D2ModdingToolset/mss32/src/diplomacyhooks.cpp`, функции `get*RelationHooked`,
`areAllies*`, `areAlwaysAtWar*`, `aiCouldNotBreakAlliance*`):

| биты | что |
|---|---|
| 0 | союз |
| 1…7 | ТЕКУЩЕЕ отношение, 0…100 |
| 8…14 | ПРОШЛОЕ отношение |
| 15…29 | ход заключения союза |
| 30 | вечная война |
| 31 | ИИ не может разорвать союз |

Отсюда ванильное `Greenskins` 20640 = `0x50A0` читается как 80/80, а ноль эталона — как ВОЙНА
(бит 0 и оба поля нулевые). Ставим `(75 << 1) | (75 << 8) = 19350`. Полосы берутся из `GVars`
и сравниваются теми же знаками, что в `areRelations*Hooked`: война `[0…D_WAR 33]`, нейтралитет
`(33…D_NEUTRAL 66]`, мир `(66…D_PEACE 100]`.

**`D_ROD` — штраф за УНИЧТОЖЕНИЕ чужого жезла, а не за постановку своего.** Исправлено
четвёртым живым прогоном 10.09.2026 (`disciples-modding/.superpowers/sdd/2026-09-09-f6plus-coverage/run4-analysis.md`,
§3.1 и §4.6); прежняя редакция абзаца — «штраф даётся за факт постановки» — неверна.
Цепочка прослежена была верно: `D_ROD` читает геттер `0x5878B9` (поле `+0xD8`), единственный
вызов — `0x5D986B` внутри `0x5D985B`, тот берёт две расы и дельту и зовёт мутатор `0x5D9366`,
а единственный вызов `0x5D985B` — `0x5DA006`. Неверен был вывод: `0x5DA006` сидит в
`0x5D9FAB`, который заканчивается `0x5DA011 call 0x5E873D` = **`CVisitorDestroyRod`**, то есть
это обработчик СНОСА. Его единственный вызывающий — `CEffectBreakRod::apply 0x433C65`, где
`0x433CC8` пропускает штраф при пустом «виновнике» (путь заклинания `L_REMOVE_ROD`) и берёт
его, когда жезл снёс отряд (`0x440CD5`). Постановка — `CEffectDropRod::apply 0x437606` —
мутатор отношений не зовёт вовсе, поэтому жезл можно ставить где угодно.

Проверено данными: за прогон поставлено четыре жезла, число записей `CMidRod` в снимках шло
1,1,1,1,1,1,2,2 и 0,1,2,2 — **ни разу не уменьшилось**, ни один жезл не был уничтожен, и все
движения отношений объяснились боями, вором, данью и союзом без участия `D_ROD`.

**Ещё две поправки того же прогона.** `D_MK_ALLY` (100), `D_BK_ALLY` (10), `D_REF_ALLY` (50)
и `D_ATTAK_PC` (66) — **присваиваемые значения, а не дельты**; порог союза — отдельная
функция `0x5DAA0E canMakeAlliance` (`relation > D_NEUTRAL`). И мутатор `0x5D9366` двигает не
только пару (A, B): каждой третьей расе X, воюющей с B, достаётся `rel(A, X) − value/2`.

**Каким лордом играть — «Повелитель Воров» `G000LR0002`.** Гейт действий вора — категория
ЛОРДА (`Glord.CATEGORY = 2 = L_DIPLOMAT`, три места: `0x5D3359`, `0x5D3A89`, `0x49AABF`), а не
класс юнита. Под Воителем против чужого ГОРОДА остаются `spy` и `poisonCity`, против отряда —
`Poison Party` и `Duel`; единственный отряд соседа стоит гарнизоном ВНУТРИ столицы и отдельной
мишенью на карте не появляется. Под Повелителем Воров открывается весь список, и в нём есть два
действия с `Gaction.CHANCE = 100` (`L_SPY` и `L_RIOT_CITY`): бросок их не заваливает, значит
и вор не гибнет (провал убивает вора безусловно, `0x44314B`), и `D_NOBLE` приходит наверняка.

**Мишень — столица соседа (26,26)**, деревень на карте нет, а его единственный отряд внутри
неё. Оба вора бьют по ней в один и тот же ход, поэтому прогон укладывается в два хода.

**Потолок этой карты.** `THIEF_ACTS_PER_TURN = 1` — допущение вслух: если движок разрешит вору
два действия за ход, хватило бы и одного вора. Второй вор — страховка, а не находка.
"""
from __future__ import annotations

import argparse
import sys
from dataclasses import replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402
import scenario_squads as sq  # noqa: E402
import scenario_c4 as sc  # noqa: E402
import make_city_map as city  # noqa: E402
import make_f7_patch_maps as mp  # noqa: E402   (add_party, rebuild_plan, players, EXPORTS)
import f7_plan as fp  # noqa: E402              (геометрия, потолки, bank_gold, raises)
import sg_second_player as sp  # noqa: E402     (race_number, relation_word, set_diplomacy)
from make_arena_map import set_bank  # noqa: E402
from dbf_dump import read as dbf_read  # noqa: E402

# ЭТАЛОН пользователя: открывается только на чтение. 11.09.2026 перенесён из Exports в архив.
SRC = mp.MODDING / "game-dev" / "Exports-archive" / "2-capital-test.sg"
KEY = "m15"

# Раскладка тела `CMidPlayer` до поля `LORD_ID` — правка на месте, как у казны.
_PLAYER_TO_LORD = "PLAYER_ID:s NAME_TXT:s DESC_TXT:s"


def _party(xy: tuple[int, int], leader: str, cat: int, why: str) -> dict:
    """Отряд игрока НА КАРТЕ: стек в столице пересоздаёт лорд, этот — переживает выбор."""
    return {"xy": xy, "leader": leader, "cat": cat, "units": [], "why": why,
            "cell": 0, "fights": False}


M15: dict = {
    "src": SRC.name,
    "file": "f7_m15_diplo.sg",
    "name": "f7-m15-diplo",
    "why": "S15: шкала отношений — D_ROD -15 за жезл и D_NOBLE -5 за каждое действие вора",
    "race": "g000rr0001",              # раса соседа из эталона: Горные кланы, RACE_TYPE 3
    "lord": "G000LR0002",              # Повелитель Воров: только под ним открыт весь список
    "bank": "G3000:R1000:Y1000:E1000:W1000:B1000",
    "relation": 75,                    # мир; после -15 станет 60 — нейтралитет, полоса сменится
    "turns": 2,                        # ход 1 — жезл, ход 2 — оба вора у чужой столицы
    "rod": (25, 4),                    # клетка жезла: шаг ВОСТОК от жезлоносца, в сторону соседа
    "noble_acts": 2,                   # по действию на вора, оба на втором ходу
    "parties": [
        _party((24, 4), fp.HERO_ROD, fp.CAT_ROD,
               "жезлоносец: жезл в (25,4), это ближайшая к соседу клетка, которую покрывает "
               "запас безопасности"),
        _party((20, 4), fp.HERO_THIEF, fp.CAT_THIEF, "вор 1: до столицы соседа два хода"),
        _party((30, 4), fp.HERO_THIEF, fp.CAT_THIEF, "вор 2: бьёт по той же столице тем же ходом"),
    ],
}


# ------------------------------------------------------------------ чтение эталона

def second_player(scen: sg.Sg, prof: dict) -> dict:
    p = sp.second_player(scen, {"race": prof["race"]})
    if p is None:
        raise SystemExit("%s: в файле нет игрока расы %s — шкалы отношений не существует"
                         % (KEY, prof["race"]))
    return p


def capital_of(scen: sg.Sg, player_id: str) -> sg.Record:
    for rec in scen.find(sg.WHAT_CAPITAL):
        if sg.fields(rec.body, sg.LAYOUT_CAPITAL)["OWNER"] == player_id:
            return rec
    raise SystemExit("%s: у игрока %s нет столицы" % (KEY, player_id))


def capital_spot(scen: sg.Sg, player_id: str) -> list[tuple[int, int]]:
    """Пятно столицы 5x5 — от него и меряются все расстояния карты."""
    return fp.capital_tiles_at(sp.city_xy(capital_of(scen, player_id)))


def field_stacks(scen: sg.Sg, owner: str) -> list[dict]:
    """Отряды игрока НА КАРТЕ: гарнизон (`INSIDE`) не в счёт, он из города не выходит по делу."""
    return [s for r in scen.find(sg.WHAT_STACK)
            for s in [sg.parse_stack(r.body)]
            if s["OWNER"] == owner and s["INSIDE"] == sg.NULL_ID]


def leader_cat(t: sq.Tables, types: dict, stack: dict) -> int:
    raw = str(t.unit(types[stack["LEADER_ID"].upper()])["LEADER_CAT"]).strip()
    return int(raw) if raw and raw != "None" else 0


def stack_xy(stack: dict) -> tuple[int, int]:
    return int(stack["POS_X"]), int(stack["POS_Y"])


def diplomacy_pairs(scen: sg.Sg) -> list[tuple[int, int, int]]:
    c = sg.Cursor(scen.one(sp.WHAT_DIPLO).body)
    return [(c.int("RACE_1"), c.int("RACE_2"), c.int("RELATION")) for _ in range(c.count())]


def pair_word(scen: sg.Sg, prof: dict) -> int:
    """Сырое слово `RELATION` пары «Империя — раса соседа»."""
    ours, theirs = sp.race_number(sc.RACE_EMPIRE), sp.race_number(prof["race"])
    for a, b, word in diplomacy_pairs(scen):
        if {a, b} == {ours, theirs}:
            return word
    raise SystemExit("%s: пары рас %d и %d нет в CMidDiplomacy" % (KEY, ours, theirs))


# --------------------------------------------------- досягаемость: шаги, ходы, найм

def steps_per_turn(move: int) -> int:
    """Клеток по равнине за ход: очки хода делить на цену шага (`PLAIN_COST` = 2, 0x6027f3)."""
    return int(move) // fp.PLAIN_COST


def turns_to_walk(move: int, gap: int) -> int:
    per = steps_per_turn(move)
    if per <= 0:
        raise SystemExit("%s: лидер с нулевым ходом — путь не посчитать" % KEY)
    return -(-gap // per)


def enemy_threats(t: sq.Tables, scen: sg.Sg, prof: dict) -> list[tuple[str, int, int]]:
    """(кто, ходов в распоряжении, клеток за ход) — всё, чем сосед может дотянуться до нас.

    Гарнизон стоит на карте с первого хода, значит ходов у него столько же, сколько у прогона.
    Наёмник появляется в тот ход, когда казна впервые покрывает `ENROLL_C`: дневной тик идёт уже
    при загрузке карты, поэтому на ходу n в кошельке `BANK + INCOME * n`. С этого хода у него
    остаётся `turns - n + 1` ходов, а не все.
    """
    second = second_player(scen, prof)
    types, turns = mp.unit_types(scen), prof["turns"]
    out = []
    for rec in scen.find(sg.WHAT_STACK):
        s = sg.parse_stack(rec.body)
        if s["OWNER"] != second["PLAYER_ID"]:
            continue
        type_id = types[s["LEADER_ID"].upper()]
        out.append(("отряд на карте %s" % type_id, turns, steps_per_turn(t.unit(type_id)["MOVE"])))
    gold = fp.bank_gold(second["BANK"])
    income = fp.bank_gold(fp.grace(prof["race"])["INCOME"])
    for row in dbf_read(sq.GLOBALS / "Gunits.dbf")["rows"]:
        if row["RACE_ID"].lower() != prof["race"].lower() or int(row["LEADER_CAT"] or 0) <= 0:
            continue
        cost = fp.bank_gold(str(row["ENROLL_C"]))
        day = next((n for n in range(1, turns + 1) if gold + income * n >= cost), None)
        if day is not None:
            out.append(("найм %s на ходу %d" % (row["UNIT_ID"], day),
                        turns - day + 1, steps_per_turn(row["MOVE"])))
    return out


def enemy_cover(t: sq.Tables, scen: sg.Sg, prof: dict) -> tuple[int, str]:
    """Сколько клеток от своей столицы сосед успевает пройти за отпущенные ходы, и кем."""
    threats = enemy_threats(t, scen, prof)
    if not threats:
        raise SystemExit("%s: у соседа нет ни отряда, ни денег на найм — модель угрозы пуста, "
                         "а пустая модель ничего не доказывает" % KEY)
    who, turns, per = max(threats, key=lambda x: x[1] * x[2])
    return turns * per, "%s: %d клеток за ход, %d ходов" % (who, per, turns)


# ------------------------------------------------------------------------ сторожа
#
# Каждый роняет сборку и каждый проверен подсадной ошибкой в `--selftest`; сторож `RELATION`
# вдобавок прогоняется на ЭТАЛОНЕ, где отношение нулевое, — он обязан завалить и его.

def check_relation(scen: sg.Sg, prof: dict, key: str = KEY) -> None:
    """Отношение пары рас: старт в полосе мира, после жезла — уже нейтралитет, и не в пол."""
    word = pair_word(scen, prof)
    cur, prev = (word >> 1) & 0x7F, (word >> 8) & 0x7F
    war, neutral, peace = (int(fp.gvars()[k]) for k in ("D_WAR", "D_NEUTRAL", "D_PEACE"))
    if cur != prof["relation"] or prev != prof["relation"]:
        raise SystemExit("%s: RELATION = %d, это %d/%d по разбору битов, а нужно %d/%d "
                         "(ноль читается как ВОЙНА)"
                         % (key, word, cur, prev, prof["relation"], prof["relation"]))
    if not neutral < cur <= peace:
        raise SystemExit("%s: старт %d вне полосы мира (%d, %d]" % (key, cur, neutral, peace))
    after_rod = cur + int(fp.gvars()["D_ROD"])
    if after_rod > neutral:
        raise SystemExit("%s: после жезла останется %d, это всё ещё мир (> %d) — полоса не "
                         "сменится и штраф глазами не увидеть" % (key, after_rod, neutral))
    left = after_rod + int(fp.gvars()["D_NOBLE"]) * prof["noble_acts"]
    if left <= war:
        raise SystemExit("%s: после жезла и %d действий вора останется %d — шкала упрётся в "
                         "войну (<= %d) и разницы не покажет"
                         % (key, prof["noble_acts"], left, war))


def check_limits(scen: sg.Sg, prof: dict, key: str = KEY) -> None:
    fp.check_scenario_limits(city.info_fields(scen), key)


def check_bank(scen: sg.Sg, prof: dict, key: str = KEY) -> None:
    """Казна игрока: ровно заданная строка и ниже потолка `Economy.CurrencyMax`."""
    human, _ = mp.players(scen)
    if human["BANK"] != prof["bank"]:
        raise SystemExit("%s: казна %s, ожидалась %s (жезл стоит %s)"
                         % (key, human["BANK"], prof["bank"], fp.gvars()["ROD_COST"]))
    for part in prof["bank"].split(":"):
        if int(part[1:]) > fp.GOLD_CAP:
            raise SystemExit("%s: казна %s выше потолка %d" % (key, prof["bank"], fp.GOLD_CAP))


def check_name(scen: sg.Sg, prof: dict, key: str = KEY) -> None:
    """Имя карты в ОБОИХ местах: список игры читает заголовок, не `ScenarioInfo`."""
    if sg.scenario_name(scen) != prof["name"]:
        raise SystemExit("%s: ScenarioInfo.NAME %r, ожидалось %r"
                         % (key, sg.scenario_name(scen), prof["name"]))
    raw = sg.header_name(scen)
    if raw != prof["name"]:
        raise SystemExit("%s: имя в заголовке %r, ожидалось %r" % (key, raw, prof["name"]))


def check_lord(scen: sg.Sg, prof: dict, key: str = KEY) -> None:
    """Лорд игрока в файле — Повелитель Воров: гейт действий вора это КАТЕГОРИЯ лорда."""
    human, _ = mp.players(scen)
    want = prof["lord"].lower()
    if human["LORD_ID"].lower() != want:
        raise SystemExit("%s: лорд карты %s, ожидался %s — под остальными вор против чужого "
                         "города умеет только spy и poisonCity"
                         % (key, human["LORD_ID"], prof["lord"]))
    cats = {r["LORD_ID"].lower(): int(r["CATEGORY"])
            for r in dbf_read(sq.GLOBALS / "Glord.dbf")["rows"]}
    if cats.get(want) != 2:
        raise SystemExit("%s: у лорда %s Glord.CATEGORY %s, а гейт открывает только 2 "
                         "(L_DIPLOMAT)" % (key, prof["lord"], cats.get(want)))


def check_parties_on_map(t: sq.Tables, scen: sg.Sg, prof: dict, key: str = KEY) -> None:
    """На карте есть жезлоносец и столько воров, сколько действий закладывает прогон."""
    human, _ = mp.players(scen)
    types = mp.unit_types(scen)
    cats = [leader_cat(t, types, s) for s in field_stacks(scen, human["PLAYER_ID"])]
    if cats.count(fp.CAT_ROD) < 1:
        raise SystemExit("%s: на карте нет отряда с LEADER_CAT %d — жезл ставить некому "
                         "(способность L_ROD только у этой категории)" % (key, fp.CAT_ROD))
    want = prof["noble_acts"]
    if cats.count(fp.CAT_THIEF) < want:
        raise SystemExit("%s: воров на карте %d при %d закладываемых действиях — за отпущенные "
                         "%d хода больше одного действия на вора не выйдет"
                         % (key, cats.count(fp.CAT_THIEF), want, prof["turns"]))


def check_thief_reach(t: sq.Tables, scen: sg.Sg, prof: dict, key: str = KEY) -> None:
    """Каждый вор доходит до мишени за отпущенные ходы — по очкам хода, а не на глаз."""
    human, _ = mp.players(scen)
    spot = capital_spot(scen, second_player(scen, prof)["PLAYER_ID"])
    types = mp.unit_types(scen)
    for s in field_stacks(scen, human["PLAYER_ID"]):
        if leader_cat(t, types, s) != fp.CAT_THIEF:
            continue
        move = int(t.unit(types[s["LEADER_ID"].upper()])["MOVE"])
        gap = min(fp.cheb(stack_xy(s), tile) for tile in spot)
        need = turns_to_walk(move, gap)
        if need > prof["turns"]:
            raise SystemExit("%s: вор в %s идёт до чужой столицы %d клеток, это %d ходов при "
                             "%d очках хода, а прогон длится %d — до мишени он не дойдёт"
                             % (key, stack_xy(s), gap, need, move, prof["turns"]))


def check_safety(t: sq.Tables, scen: sg.Sg, prof: dict, key: str = KEY) -> None:
    """Сосед не успевает дойти до наших отрядов НА КАРТЕ за отпущенные ходы.

    Столица в счёт не идёт: она стоит там, где её поставил пользователь, и у неё свой гарнизон;
    прогон её не использует. Меряется от пятна чужой столицы — оттуда выходит любой его отряд.
    """
    cover, who = enemy_cover(t, scen, prof)
    human, _ = mp.players(scen)
    spot = capital_spot(scen, second_player(scen, prof)["PLAYER_ID"])
    for s in field_stacks(scen, human["PLAYER_ID"]):
        gap = min(fp.cheb(stack_xy(s), tile) for tile in spot)
        if gap <= cover:
            raise SystemExit("%s: наш отряд в %s стоит в %d клетках от чужой столицы, а сосед "
                             "покрывает %d (%s) — успеет прийти и убить"
                             % (key, stack_xy(s), gap, cover, who))


def check_rod_cell(scen: sg.Sg, prof: dict, key: str = KEY) -> None:
    """Клетка жезла: равнина, свободна, не ближе клетки к городу, жезлоносец рядом."""
    cell = tuple(prof["rod"])
    grid = sg.tile_grid(scen)
    if (grid[cell[1]][cell[0]] >> 3) != 0:
        raise SystemExit("%s: клетка жезла %s не равнина (грунт %d) — движок откажет "
                         "(0x6137FE)" % (key, cell, grid[cell[1]][cell[0]] >> 3))
    towns = [xy for rec in scen.find(sg.WHAT_CAPITAL)
             for xy in fp.capital_tiles_at(sp.city_xy(rec))]
    gap = min(fp.cheb(cell, xy) for xy in towns)
    if gap < fp.ROD_CITY_GAP:
        raise SystemExit("%s: клетка жезла %s в %d клетках от города, движок запрещает коробку "
                         "3x3 (0x6137FE)" % (key, cell, gap))
    occupied = {stack_xy(sg.parse_stack(r.body)) for r in scen.find(sg.WHAT_STACK)}
    if cell in occupied or cell in set(towns):
        raise SystemExit("%s: клетка жезла %s занята — ставить некуда" % (key, cell))
    human, _ = mp.players(scen)
    near = [s for s in field_stacks(scen, human["PLAYER_ID"])
            if fp.cheb(stack_xy(s), cell) <= 1]
    if not near:
        raise SystemExit("%s: рядом с клеткой жезла %s нет ни одного нашего отряда — шаг на "
                         "неё делать некому" % (key, cell))


def check_plan(scen: sg.Sg, prof: dict, key: str = KEY) -> None:
    """Каждый отряд стоит в плане карты своей клеткой: без записи плана объекта нет."""
    _, entries = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    seen = {(x, y, e) for x, y, e in entries}
    for rec in scen.find(sg.WHAT_STACK):
        s = sg.parse_stack(rec.body)
        if (stack_xy(s) + (rec.obj_id,)) not in seen:
            raise SystemExit("%s: отряда %s в %s нет в плане карты"
                             % (key, rec.obj_id, stack_xy(s)))


# -------------------------------------------------------------------------- сборка

def set_lord(scen: sg.Sg, player_id: str, lord: str) -> sg.Sg:
    """`LORD_ID` игрока на месте: голова тела копируется, поле пересобирается, хвост цел."""
    for rec in scen.find(sg.WHAT_PLAYER):
        if sg.fields(rec.body, "PLAYER_ID:s")["PLAYER_ID"] != player_id:
            continue
        c = sg.Cursor(rec.body)
        c.fields(_PLAYER_TO_LORD)
        start = c.pos
        c.str("LORD_ID")
        body = rec.body[:start] + sg.f_str("LORD_ID", lord) + rec.body[c.pos:]
        return scen.with_record(rec, replace(rec, body=body))
    raise SystemExit("%s: записи игрока %s нет" % (KEY, player_id))


def set_relation(scen: sg.Sg, prof: dict) -> sg.Sg:
    """Переписать `RELATION` ТОЛЬКО у пары двух игроков; пары с нейтралом остаются нулями."""
    ours, theirs = sp.race_number(sc.RACE_EMPIRE), sp.race_number(prof["race"])
    word = sp.relation_word(prof["relation"])
    pairs, hit = [], 0
    for a, b, old in diplomacy_pairs(scen):
        if {a, b} == {ours, theirs}:
            pairs.append((a, b, word))
            hit += 1
        else:
            pairs.append((a, b, old))
    if hit != 1:
        raise SystemExit("%s: пара рас %d и %d встретилась %d раз, ожидалась одна"
                         % (KEY, ours, theirs, hit))
    return sp.set_diplomacy(scen, pairs)


def build(src: Path, t: sq.Tables, prof: dict = M15) -> tuple[sg.Sg, dict]:
    """Заплата поверх эталона: отношение, потолки, казна, лорд и три отряда на карте."""
    scen = sg.Sg.read(src)
    human, _ = mp.players(scen)
    owner = human["PLAYER_ID"]
    subrace = str(sg.fields(capital_of(scen, owner).body, sg.LAYOUT_CAPITAL)["SUBRACE"])
    scen = set_relation(scen, prof)
    scen = set_bank(scen, owner, prof["bank"])
    scen = set_lord(scen, owner, prof["lord"])
    for field, value in (("MAX_UNIT", fp.MAX_UNIT_WANTED),
                         ("MAX_LEADER", fp.MAX_LEADER_WANTED),
                         ("MAX_SPELL", fp.MAX_SPELL_WANTED)):
        scen = city.set_info_int(scen, field, value)
    for party in prof["parties"]:
        scen = mp.add_party(scen, party, owner, subrace, t)
    scen = mp.rebuild_plan(scen)
    scen = sg.set_scenario_name(scen, prof["name"])
    return scen, {"записей": len(scen.records), "отрядов добавлено": len(prof["parties"]),
                  "RELATION": sp.relation_word(prof["relation"])}


def verify(path: Path, t: sq.Tables, prof: dict = M15, quiet: bool = False) -> None:
    """Прочитать записанный файл обратно и проверить числами всё, что сборка обещала."""
    scen = sg.Sg.read(path)          # чтение сверяет счётчик объектов и рамки записей
    check_name(scen, prof)
    check_limits(scen, prof)
    check_bank(scen, prof)
    check_lord(scen, prof)
    check_relation(scen, prof)
    check_parties_on_map(t, scen, prof)
    check_thief_reach(t, scen, prof)
    check_safety(t, scen, prof)
    check_rod_cell(scen, prof)
    check_plan(scen, prof)
    if not quiet:
        print("  проверка чтения %s: имя в обоих местах, потолки, казна, лорд, отношение, "
              "жезлоносец и воры, пути и безопасность, план" % prof["file"])


# -------------------------------------------------------------------------- печать

def print_plan(t: sq.Tables, scen: sg.Sg, prof: dict) -> None:
    """Числа прогона: упаковка отношения, расстояния, ходы и модель угрозы соседа."""
    word = sp.relation_word(prof["relation"])
    rod, noble = int(fp.gvars()["D_ROD"]), int(fp.gvars()["D_NOBLE"])
    print("%s -> %-14s %s" % (KEY, prof["name"], prof["why"]))
    print("   RELATION = %d (0x%04X): текущее %d, прошлое %d, союз %d"
          % (word, word, (word >> 1) & 0x7F, (word >> 8) & 0x7F, word & 1))
    print("   шкала: %d (мир) -> жезл %+d -> %d (нейтралитет, граница %s) -> %d действий вора "
          "%+d -> %d" % (prof["relation"], rod, prof["relation"] + rod, fp.gvars()["D_NEUTRAL"],
                         prof["noble_acts"], noble * prof["noble_acts"],
                         prof["relation"] + rod + noble * prof["noble_acts"]))
    print("   казна %s, потолки MAX_UNIT %d / MAX_LEADER %d / MAX_SPELL %d, лорд %s"
          % (prof["bank"], fp.MAX_UNIT_WANTED, fp.MAX_LEADER_WANTED, fp.MAX_SPELL_WANTED,
             prof["lord"]))
    second = second_player(scen, prof)
    spot = capital_spot(scen, second["PLAYER_ID"])
    cover, who = enemy_cover(t, scen, prof)
    print("   мишень: столица соседа %s, казна соседа %s"
          % (sp.city_xy(capital_of(scen, second["PLAYER_ID"])), second["BANK"]))
    for party in prof["parties"]:
        move = int(t.unit(party["leader"])["MOVE"])
        gap = min(fp.cheb(tuple(party["xy"]), tile) for tile in spot)
        print("   отряд %-8s %-14s ход %2d (%d клеток за ход), до мишени %d клеток = %d ходов "
              "— %s" % (tuple(party["xy"]), t.name(party["leader"]), move, steps_per_turn(move),
                        gap, turns_to_walk(move, gap), party["why"]))
    print("   жезл %s, до ближайшего города %d клеток при запрете %d"
          % (tuple(prof["rod"]),
             min(fp.cheb(tuple(prof["rod"]), xy) for rec in scen.find(sg.WHAT_CAPITAL)
                 for xy in fp.capital_tiles_at(sp.city_xy(rec))), fp.ROD_CITY_GAP))
    print("   угроза соседа за %d хода: %d клеток (%s)" % (prof["turns"], cover, who))
    for name, turns, per in enemy_threats(t, scen, prof):
        print("      %-34s %d ходов x %d клеток = %d" % (name, turns, per, turns * per))


# ------------------------------------------------------------------------ selftest

def sabotage(scen: sg.Sg, what: str, prof: dict) -> sg.Sg:
    """Подсадная ошибка: ровно одно обещание за раз, всё остальное честное."""
    human, _ = mp.players(scen)
    if what == "relation":
        return set_relation(scen, dict(prof, relation=0))
    if what == "maxunit":
        return city.set_info_int(scen, "MAX_UNIT", 2)
    if what == "bank":
        return set_bank(scen, human["PLAYER_ID"], "G0100:R0000:Y0000:E0000:W0000:B0000")
    if what == "lord":
        return set_lord(scen, human["PLAYER_ID"], "G000LR0001")
    if what == "name":
        return sg.set_scenario_name(scen, "not-m15")
    if what in ("no_rod", "no_thief", "thief_far", "thief_near"):
        return _sabotage_stack(scen, what, prof)
    raise SystemExit("нет подсадной ошибки %s" % what)


def _sabotage_stack(scen: sg.Sg, what: str, prof: dict) -> sg.Sg:
    """Порча отрядов: убрать жезлоносца, убрать вора, увести вора далеко или под нож соседа."""
    t = sq.Tables()
    human, _ = mp.players(scen)
    types = mp.unit_types(scen)
    mine = [r for r in scen.find(sg.WHAT_STACK)
            if sg.parse_stack(r.body)["OWNER"] == human["PLAYER_ID"]
            and sg.parse_stack(r.body)["INSIDE"] == sg.NULL_ID]
    cat = {r.obj_id: leader_cat(t, types, sg.parse_stack(r.body)) for r in mine}
    drop = next(r for r in mine
                if cat[r.obj_id] == (fp.CAT_ROD if what == "no_rod" else fp.CAT_THIEF))
    if what in ("no_rod", "no_thief"):
        return replace(scen, records=tuple(r for r in scen.records if r is not drop))
    xy = (0, 0) if what == "thief_far" else tuple(capital_spot(
        scen, second_player(scen, prof)["PLAYER_ID"])[0])
    return _move_stack(scen, drop, (xy[0], xy[1] - 6) if what == "thief_near" else xy)


def _move_stack(scen: sg.Sg, rec: sg.Record, xy: tuple[int, int]) -> sg.Sg:
    """Переставить отряд: в теле меняются только `POS_X`/`POS_Y`, хвост копируется байт в байт."""
    c = sg.Cursor(rec.body)
    c.fields(sg._GROUP)
    for _ in range(c.count()):
        c.str("ITEM_ID")
    c.fields("STACK_ID:s SRCTMPL_ID:s LEADER_ID:s LEADR_ALIV:b")
    start = c.pos
    c.int("POS_X")
    c.int("POS_Y")
    body = rec.body[:start] + sg.f_int("POS_X", xy[0]) + sg.f_int("POS_Y", xy[1]) + rec.body[c.pos:]
    scen = scen.with_record(rec, replace(rec, body=body))
    return mp.rebuild_plan(scen)


SABOTAGE = [
    ("relation", "RELATION остался нулевым"),
    ("maxunit", "MAX_UNIT не 5"),
    ("bank", "казна осталась эталонной"),
    ("lord", "лорд не Повелитель Воров"),
    ("name", "имя карты не сменилось"),
    ("no_rod", "жезлоносца нет"),
    ("no_thief", "вора нет"),
    ("thief_far", "вор не дотягивается до мишени за отпущенные ходы"),
    ("thief_near", "сосед дотягивается до нашего отряда"),
]


def selftest(src: Path, scratch: Path) -> int:
    """Сторожа проверяются подсадной ошибкой, а сторож отношения — ещё и на ЭТАЛОНЕ."""
    t = sq.Tables()
    scratch.mkdir(parents=True, exist_ok=True)
    out = scratch / "f7_m15_selftest.sg"
    scen, _ = build(src, t)
    scen.write(out)
    honest = fp.raises(lambda: verify(out, t, quiet=True))
    if honest is not None:
        raise SystemExit("честная карта не проходит собственную проверку: %s" % honest)
    cases = [("эталон %s: RELATION нулевой" % src.name,
              fp.raises(lambda: check_relation(sg.Sg.read(src), M15, "эталон")))]
    for what, title in SABOTAGE:
        sabotage(scen, what, M15).write(out)
        cases.append((title, fp.raises(lambda: verify(out, t, quiet=True))))
    cases += training_cases(t)
    out.unlink()
    bad = 0
    for name, msg in cases:
        print("%-52s %s" % (name, ("ловится: " + msg.split("\n")[0]) if msg else "ПРОПУЩЕН"))
        bad += msg is None
    print("selftest: сработали %d сторожей из %d сравнений" % (len(cases) - bad, len(cases)))
    if not cases:
        raise SystemExit("selftest не сделал ни одного сравнения — это не проверка")
    return 1 if bad else 0


def training_cases(t: sq.Tables) -> list[tuple[str, str | None]]:
    """Задача 2: в отряде обучения обязан стоять юнит с ДРУГОЙ ставкой `TRAINING_C`."""
    m11 = fp.profile("m11")
    flat = dict(m11, train=[(u, m) for u, m in m11["train"] if u != fp.KNIGHT],
                parties=[dict(p, units=[(u, hp) for u, hp in p["units"] if u != fp.KNIGHT])
                         for p in m11["parties"]])
    absent = dict(m11, parties=[dict(p, units=[(u, hp) for u, hp in p["units"]
                                               if u != fp.KNIGHT]) for p in m11["parties"]])
    return [
        ("m11: в отряде обучения одна ставка на всех",
         fp.raises(lambda: fp.check_training_rates(t, flat, "m11"))),
        ("m11: обучаемого юнита нет в отряде",
         fp.raises(lambda: fp.check_training_rates(t, absent, "m11"))),
    ]


# ------------------------------------------------------------------------ main

def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--src", type=Path, default=SRC, help="эталон пользователя, только чтение")
    p.add_argument("--out-dir", type=Path, default=mp.EXPORTS)
    p.add_argument("--plan", action="store_true", help="только расчёт, ничего не записывать")
    p.add_argument("--selftest", action="store_true", help="сторожа подсадной ошибкой")
    p.add_argument("--scratch", type=Path,
                   default=Path(__file__).resolve().parents[1] / "corpus" / "_f7-selftest")
    a = p.parse_args(argv)
    if not a.src.exists():
        raise SystemExit("нет эталона %s" % a.src)
    if a.selftest:
        return selftest(a.src, a.scratch)
    t = sq.Tables()
    scen, stats = build(a.src, t)
    print_plan(t, scen, M15)
    if a.plan:
        return 0
    out = a.out_dir / M15["file"]
    scen.write(out)
    print("%s: %s" % (out, ", ".join("%s %s" % kv for kv in stats.items())))
    verify(out, t)
    return 0


if __name__ == "__main__":
    sys.exit(main())
