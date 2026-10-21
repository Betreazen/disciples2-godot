#!/usr/bin/env python3
"""Карта M22 `f7-m22-capture3` — переход казны при захвате столицы, когда соперник НЕ последний.

    python tools/make_f7_m22_map.py             # записать game-dev/Exports/f7_m22_capture3.sg
    python tools/make_f7_m22_map.py --plan      # расчёт боёв и таблица ожиданий, без записи
    python tools/make_f7_m22_map.py --selftest  # порча файла плюс сторож

**Почему M20 не сработала.** На `f7-m20-capture-mana` соперник был один: после захвата его
столицы игра объявила победу, показала аутро и вышла в меню — в журнале после `battle_end`
ни одного события. Здесь играбельных рас три: мы (Империя), ЖЕРТВА (Легионы, столица берётся)
и СВИДЕТЕЛЬ (Эльфы, живёт дальше, поэтому сценарий не кончается).

**Основа — ванильная `Arrival of the Pilgrims.sg`, а не `2-capital-test.sg` плюс третий игрок.**
Среди карт `Exports-archive` и `Exports` ровно три играбельные расы с Империей и таблицей рас
заголовка, которую читает `sg_second_player`, только у двух: `Arrival of the Pilgrims`
(Империя, Легионы, Эльфы) и `Vaglan 2` (у Легионов там шесть городов и два отряда). У `Ice-Mar`
и `Lunnark's Travels` заголовок другого формата. В `Arrival` все восемь городов нейтральные, у
Легионов и Эльфов ни одного отряда на карте, потолки уже 5/99/5 — жертва по самому строгому
условию гибели готова без правки. Третьего игрока пришлось бы ПИСАТЬ `sg_second_player`, а его
запись роняла игру; после починки (`9d887bb`) живьём она не проверялась, M15 ушла на карту
пользователя именно поэтому. Здесь все три игрока записаны движком: их тела сверяются с ванилью
байт в байт (кроме `BANK`), а сборка только УДАЛЯЕТ объекты и дописывает два отряда тем же
писателем `add_party`, что у всех карт Ф7.

**Что снято.** Все 16 `CMidEvent` (интро, «Z - ЛЕГИОН - ПОБЕДА» и т. п. — условия победы
сценария живут в событиях), наши отряды на карте и нейтральные отряды ближе `CLEAR` клеток к
отряду захвата. Записи `ET` и `QL` — это `CMidSpellEffects` и `CMidQuestLog`; оба в основе
пусты (счётчик 0), сторож это держит. План карты НЕ пересобирается общим `rebuild_plan`: тот
знает не все классы и выбросил бы дороги, руины и ландшафт — из плана удаляются только
снятые объекты и дописываются новые отряды.

**Бой — две битвы и броня 90.** Живой M20 (`corpus/20260911-passive/passive-003.jsonl`):
сначала бой с отрядом-гарнизоном (`King's Guard`: `armor ret 50` = своя 0 + укрепление 50),
потом ОТДЕЛЬНЫЙ бой со столицей, где страж один: `armor ret 90` = min(90, своя 50 + 50). Модель
`f7_twocap.battle` берёт `max(своя, укрепление)` = 50 и дала 4 раунда; вышло 15 (удары
Друллианов по 18 вместо 88). Здесь броня защитника — `min(90, своя + укрепление)`, сторож
калибровки требует, чтобы модель на составе M20 давала не меньше живых 15 раундов.
Игра отсюда не запускается.
"""
from __future__ import annotations

import argparse
import math
import sys
from dataclasses import replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402
import scenario_c4 as sc  # noqa: E402
import scenario_squads as sq  # noqa: E402
import make_city_map as city  # noqa: E402
import make_f7_patch_maps as mp  # noqa: E402
import make_f7_m15_map as m15  # noqa: E402
import f7_plan as fp  # noqa: E402
import f7_twocap as tw  # noqa: E402
import sg_second_player as sp  # noqa: E402

KEY = "m22"
SRC = city.ARCHIVE / "Arrival of the Pilgrims.sg"
US, VICTIM, WITNESS = sc.RACE_EMPIRE, "G000RR0002", "G000RR0005"   # Империя, Легионы, Эльфы
LEADER, SOLDIER = "g000uu8103", "g000uu8003"     # Друллиан: лидер (MOVE 30, LEADERSHIP 6), солдат
PARTY_XY = (7, 36)                               # шаг наружу от входа столицы Легионов (6,6)+4
WITNESS_XY = (50, 68)                            # шаг наружу от входа столицы Эльфов (49,67)
WITNESS_LEADER = "g000uu8009"                    # Forest Liege = Grace.LEADER_1 Эльфов
CLEAR = 8                     # нейтральные отряды ближе этого к отряду захвата сняты
AI_TURNS = 2                  # прогон отдаёт ИИ один ход; второй — запас
ROAD_FACTOR = 2               # по дороге шаг вдвое дешевле равнины: худший случай на один ход
ROUNDS_SAFETY = 1.25          # запас на раунды сверх пессимистичной модели
LIVE_M20 = ("g000rr0001", 15)  # раса стража M20 (Vithar) и раундов в живом бою с ним
PLAYABLE = 3
LOW_BANK = 100
EVENT = "MidEvent"
QUEST = "MidQuestLog"
SPELL_EFFECTS = "MidSpellEffects"
RESOURCES = ("G золото", "R преисподняя", "Y жизнь", "E смерть", "W руны", "B роща")

PROFILE: dict = {
    "key": KEY, "file": "f7_m22_capture3.sg", "name": "f7-m22-capture3",
    "bank": "G0000:R0000:Y0000:E0000:W0000:B0000",
    "victim_bank": "G0500:R0333:Y0444:E0555:W0666:B0777",
    "witness_bank": "G0210:R0220:Y0230:E0240:W0250:B0260",
    "reach_turns": 1,
    "parties": [tw.party(PARTY_XY, LEADER, (SOLDIER,) * 5, cell=1,
                         why="захват: шесть Друллианов в шаге от входа", exempt=("capital",))],
    "witness_parties": [tw.party(WITNESS_XY, WITNESS_LEADER, why="свидетелю есть чем жить")],
}


# ------------------------------------------------------------------------ чтение

def by_race(scen: sg.Sg, race: str) -> dict | None:
    return next((p for p in sp.players_of(scen) if p["RACE_ID"].upper() == race.upper()), None)


def players3(scen: sg.Sg) -> tuple[dict, dict, dict]:
    """(мы, жертва, свидетель) — по расе; нет кого-то — это уже не та карта."""
    got = [by_race(scen, r) for r in (US, VICTIM, WITNESS)]
    if None in got:
        raise SystemExit("%s: нет игрока одной из рас %s" % (KEY, (US, VICTIM, WITNESS)))
    return got[0], got[1], got[2]


def playable(scen: sg.Sg) -> list[dict]:
    return [p for p in sp.players_of(scen) if p["RACE_ID"].upper() != sc.RACE_NEUTRAL.upper()]


def neutral_id(scen: sg.Sg) -> str:
    return by_race(scen, sc.RACE_NEUTRAL)["PLAYER_ID"]


def villages_of(scen: sg.Sg, pid: str) -> list[sg.Record]:
    el = city._has_p_o_el(scen)
    return [r for r in scen.find(city.WHAT_VILLAGE) if city.parse_village(r.body, el)["OWNER"] == pid]


def capitals_of(scen: sg.Sg, pid: str) -> list[sg.Record]:
    return [r for r in scen.find(sg.WHAT_CAPITAL)
            if sg.fields(r.body, sg.LAYOUT_CAPITAL)["OWNER"] == pid]


def garrison(t: sq.Tables, scen: sg.Sg, pid: str) -> list[str]:
    sid = sg.fields(tw.capital_of(scen, pid).body, sg.LAYOUT_CAPITAL)["STACK"]
    rec = next((r for r in scen.find(sg.WHAT_STACK) if r.obj_id == sid), None)
    return [] if rec is None else [u for u, _, _ in tw.stack_units(t, scen, sg.parse_stack(rec.body))]


def capital_group(t: sq.Tables, scen: sg.Sg, pid: str, race: str) -> list[str]:
    """Защитники самой столицы — группа `UNIT_0…5` тела `CCapital` (в основе там один страж
    расы записью `CMidUnit`). Если стража в файле нет, он всё равно выходит в бой — добавляем."""
    c = sg.Cursor(tw.capital_of(scen, pid).body)
    c.fields(sg.LAYOUT_CAPITAL)
    g = c.fields(sg._GROUP)
    types = mp.unit_types(scen)
    units = [types[g["UNIT_%d" % k].upper()] for k in range(6) if g["UNIT_%d" % k] != sg.NULL_ID]
    guardian = fp.grace(race)["GUARDIAN"].lower()
    return units if guardian in units else units + [guardian]


def list_count(scen: sg.Sg, what: str) -> int:
    """Длина списка записи вида «id + u32 счётчик» (`CMidQuestLog`, `CMidSpellEffects`)."""
    return sg.Cursor(scen.one(what).body).count()


# ------------------------------------------------------------------------ запись

def stack_family(rec: sg.Record) -> set[str]:
    """Отряд, его юниты и вещи — удаляются только вместе."""
    s = sg.parse_stack(rec.body)
    return ({rec.obj_id} | {s["UNIT_%d" % k] for k in range(6) if s["UNIT_%d" % k] != sg.NULL_ID}
            | set(s["ITEMS"]))


def drop_objects(scen: sg.Sg, ids: set[str]) -> sg.Sg:
    """Снять записи и ТОЛЬКО их клетки плана: дороги, руины и ландшафт ванили остаются."""
    scen = replace(scen, records=tuple(r for r in scen.records if r.obj_id not in ids))
    rec = scen.one(sg.WHAT_PLAN)
    size, plan = sg.parse_plan(rec.body)
    body = sg.plan_body(rec.obj_id, size, [e for e in plan if e[2] not in ids])
    return scen.with_record(rec, replace(rec, body=body))


def add_stacks(scen: sg.Sg, parties: list[dict], owner: str, t: sq.Tables) -> sg.Sg:
    """Отряды писателем `add_party` плюс их клетки в плане. Подраса — столицы владельца, у
    нейтрала (столицы нет) — первая его запись `CMidSubRace`."""
    caps = capitals_of(scen, owner)
    sub = (str(sg.fields(caps[0].body, sg.LAYOUT_CAPITAL)["SUBRACE"]) if caps else
           next(r.obj_id for r in scen.find(sg.WHAT_SUBRACE)
                if sg.fields(r.body, sg.LAYOUT_SUBRACE)["PLAYER_ID"] == owner))
    before = {r.obj_id for r in scen.find(sg.WHAT_STACK)}
    for p in parties:
        scen = mp.add_party(scen, p, owner, sub, t)
    new = [r for r in scen.find(sg.WHAT_STACK) if r.obj_id not in before]
    return sg.add_plan_entries(scen, [tw.xy_of(sg.parse_stack(r.body)) + (r.obj_id,) for r in new])


def move_stack(scen: sg.Sg, rec: sg.Record, xy: tuple[int, int]) -> sg.Sg:
    """Переставить отряд вместе с его клеткой плана (для порчи в `--selftest`)."""
    plan = scen.one(sg.WHAT_PLAN)
    size, entries = sg.parse_plan(plan.body)
    rest = [e for e in entries if e[2] != rec.obj_id]
    scen = scen.with_record(plan, replace(plan, body=sg.plan_body(plan.obj_id, size, rest)))
    scen = tw.patch_stack(scen, rec, POS_X=xy[0], POS_Y=xy[1])
    return sg.add_plan_entries(scen, [(xy[0], xy[1], rec.obj_id)])


def strip(scen: sg.Sg) -> sg.Sg:
    """События, наши отряды на карте и нейтралы у места захвата — прочь."""
    us, _, _ = players3(scen)
    gone = {r.obj_id for r in scen.find(EVENT)}
    for rec, s in tw.field_stacks(scen, us["PLAYER_ID"]):
        gone |= stack_family(rec)
    for rec, s in tw.field_stacks(scen, neutral_id(scen)):
        if fp.cheb(tw.xy_of(s), PARTY_XY) <= CLEAR:
            gone |= stack_family(rec)
    return drop_objects(scen, gone)


def build(t: sq.Tables, src: Path = SRC, prof: dict = PROFILE) -> sg.Sg:
    scen = sg.Sg.read(src)
    for field, value in tw.LIMITS:
        scen = city.set_info_int(scen, field, value)
    scen = strip(scen)
    us, victim, witness = players3(scen)
    for pid, bank in ((us["PLAYER_ID"], prof["bank"]), (victim["PLAYER_ID"], prof["victim_bank"]),
                      (witness["PLAYER_ID"], prof["witness_bank"])):
        scen = mp.set_bank(scen, pid, bank)
    scen = add_stacks(scen, prof["witness_parties"], witness["PLAYER_ID"], t)
    scen = add_stacks(scen, prof["parties"], us["PLAYER_ID"], t)
    return sg.set_scenario_name(scen, prof["name"])


# ------------------------------------------------------------------------ бой

def defender_armor(t: sq.Tables, unit: str, fort: int) -> int:
    """Броня защитника в укреплении: своя ПЛЮС укрепление, потолок 90 (живой M20, шапка)."""
    return min(tw.ARMOR_CAP, int(t.unit(unit)["ARMOR"] or 0) + fort)


def fight(t: sq.Tables, ours: list[tuple[str, bool, int]], foes: list[str], fort: int) -> dict:
    """Раунды до победы (наш урон без добавки, точность множителем) и худший входящий урон за
    раунд на каждого нашего (добавка максимальная, без промахов, все удары в одного)."""
    strikers = [u for u, front, _ in ours if front or not t.is_melee(u)]
    power = {u: int((tw._attack(t, u) or {"POWER": 0})["POWER"]) / 100.0 for u in strikers}
    single = [u for u in strikers if int((tw._attack(t, u) or {"REACH": 0})["REACH"]) != 1]
    dmg = {f: sum(tw.cut(tw.hit(t, u, False), defender_armor(t, f, fort)) * power[u]
                  for u in strikers) for f in foes}
    hp = {f: int(t.unit(f)["HIT_POINT"]) for f in foes}
    if not foes or any(v <= 0 for v in dmg.values()):
        rounds = None
    elif single:
        rounds = sum(math.ceil(hp[f] / dmg[f]) for f in foes)
    else:
        rounds = max(math.ceil(hp[f] / dmg[f]) for f in foes)
    incoming = [sum(tw.cut(tw.hit(t, f, True), int(t.unit(u)["ARMOR"] or 0)) for f in foes)
                for u, _, _ in ours]
    return {"rounds": rounds, "incoming": incoming, "damage": dmg}


def capture(t: sq.Tables, ours: list[tuple[str, bool, int]], garrison_units: list[str],
            inner: list[str], race: str) -> list[tuple[str, dict]]:
    """Две битвы подряд: отряд-гарнизон, потом сама столица (страж, полные HP из таблиц)."""
    fort = tw.capital_floor(race)
    return [("гарнизон", fight(t, ours, garrison_units, fort)),
            ("столица", fight(t, ours, inner, fort))]


def check_capture(ch: tw.Pairs, t: sq.Tables, ours: list[tuple[str, bool, int]],
                  garrison_units: list[str], inner: list[str],
                  race: str) -> list[tuple[str, dict]]:
    ch.is_true("у отряда захвата есть юниты", bool(ours))
    ch.is_true("у жертвы непустой гарнизон столицы", bool(garrison_units))
    fights = capture(t, ours, garrison_units, inner, race)
    for title, b in fights:
        ch.is_true("бой «%s» кончается" % title, b["rounds"] is not None, str(b["damage"]))
    if any(b["rounds"] is None for _, b in fights):
        return fights
    for k, (unit, _, hp) in enumerate(ours):
        taken = sum(b["rounds"] * b["incoming"][k] for _, b in fights) * ROUNDS_SAFETY
        ch.is_true("%s переживает обе битвы" % t.name(unit), taken < hp * (1.0 - fp.HP_MARGIN),
                   "%.0f урона (раунды x %.2f) при %d HP, запас %.0f %%"
                   % (taken, ROUNDS_SAFETY, hp, 100 * fp.HP_MARGIN))
    return fights


def m20_rounds(t: sq.Tables) -> int | None:
    """Модель на составе живого M20: три Друллиана против одного Vithar в столице Кланов."""
    ours = [(LEADER, True, 2000), (SOLDIER, True, 2000), (SOLDIER, False, 2000)]
    inner = [fp.grace(LIVE_M20[0])["GUARDIAN"].lower()]
    return capture(t, ours, [], inner, LIVE_M20[0])[1][1]["rounds"]


# ------------------------------------------------------------------------ сторожа

def check_common(ch: tw.Pairs, scen: sg.Sg, prof: dict) -> None:
    info = city.info_fields(scen)
    ch.eq("MAX_UNIT", int(info["MAX_UNIT"]), fp.MAX_UNIT_WANTED)
    ch.guard("потолки сценария 5/99/5", lambda: fp.check_scenario_limits(info, KEY))
    ch.eq("ScenarioInfo.NAME", sg.scenario_name(scen), prof["name"])
    raw = sg.header_name(scen)
    ch.eq("имя в заголовке", raw, prof["name"])
    ch.guard("расы трёх игроков в обоих ростерах",
             lambda: sp._check_rosters(scen, KEY, playable(scen)))
    ch.guard("по записи тумана/заклинаний/построек на игрока", lambda: sp._check_records(scen, KEY))
    ch.guard("гарнизоны столиц непусты и помечены INSIDE",
             lambda: sp._check_capitals(scen, KEY, {"race": WITNESS}))
    ch.guard("все отряды в плане", lambda: m15.check_plan(scen, prof, KEY))
    _, plan = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    ch.eq("висячих ссылок в плане", len({e for _, _, e in plan} - {r.obj_id for r in scen.records}), 0)


def owned(scen: sg.Sg, p: dict) -> dict[str, bytes]:
    """Семь классов игрока: игрок, туман, заклинания, постройки, подрасы, столица, гарнизон."""
    ids = {p["PLAYER_ID"], p["FOG_ID"], p["KNOWN_ID"], p["BUILDS_ID"]}
    ids |= {r.obj_id for r in scen.find(sg.WHAT_SUBRACE)
            if sg.fields(r.body, sg.LAYOUT_SUBRACE)["PLAYER_ID"] == p["PLAYER_ID"]}
    for cap in capitals_of(scen, p["PLAYER_ID"]):
        ids.add(cap.obj_id)
        sid = sg.fields(cap.body, sg.LAYOUT_CAPITAL)["STACK"]
        ids |= {sid} | next((stack_family(r) for r in scen.find(sg.WHAT_STACK) if r.obj_id == sid), set())
    return {r.obj_id: r.body for r in scen.records if r.obj_id in ids}


def check_vanilla(ch: tw.Pairs, scen: sg.Sg, src: sg.Sg) -> None:
    """Состав и тела записей трёх игроков — байт в байт как в ванили (казна обнулена в обоих)."""
    zero = "G0000:R0000:Y0000:E0000:W0000:B0000"
    for p in playable(src):
        got = by_race(scen, p["RACE_ID"])
        ch.is_true("игрок расы %s на месте" % p["RACE_ID"], got is not None)
        if got is None:
            continue
        a = owned(mp.set_bank(src, p["PLAYER_ID"], zero), p)
        b = owned(mp.set_bank(scen, p["PLAYER_ID"], zero), got)
        ch.eq("%s: состав записей игрока как в ванили" % p["RACE_ID"], sorted(b), sorted(a))
        ch.eq("%s: тела записей как в ванили" % p["RACE_ID"],
              [k for k in a if a[k] != b.get(k)], [])
    ch.eq("CMidDiplomacy как в ванили (6 пар)", scen.one(sp.WHAT_DIPLO).body,
          src.one(sp.WHAT_DIPLO).body)
    ch.eq("ростер ScenarioInfo как в ванили", sp.roster(scen), sp.roster(src))
    ch.eq("таблица рас заголовка как в ванили", sp.header_races(scen), sp.header_races(src))


def check_dangling(ch: tw.Pairs, scen: sg.Sg, src: sg.Sg) -> None:
    gone = {r.obj_id for r in src.records} - {r.obj_id for r in scen.records}
    bad = [(r.obj_id, g) for r in scen.records for g in gone if g.encode() in r.body]
    ch.is_true("снято объектов основы", bool(gone), "ничего не снято — события остались?")
    ch.eq("ссылок на снятые объекты", bad[:3], [])


def check_no_end(ch: tw.Pairs, scen: sg.Sg) -> None:
    """Захват не выигрывает сценарий, жертва гибнет, свидетелю есть чем жить."""
    _, victim, witness = players3(scen)
    ch.eq("событий сценария (условия победы)", len(scen.find(EVENT)), 0)
    ch.eq("записей журнала заданий QL", list_count(scen, QUEST), 0)
    ch.eq("действующих эффектов заклинаний ET", list_count(scen, SPELL_EFFECTS), 0)
    vid, wid = victim["PLAYER_ID"], witness["PLAYER_ID"]
    ch.eq("столиц у жертвы", len(capitals_of(scen, vid)), 1)
    ch.eq("городов у жертвы", len(villages_of(scen, vid)), 0)
    ch.eq("отрядов жертвы на карте", len(tw.field_stacks(scen, vid)), 0)
    ch.eq("столиц у свидетеля", len(capitals_of(scen, wid)), 1)
    ch.is_true("у свидетеля отряд на карте или город",
               len(tw.field_stacks(scen, wid)) + len(villages_of(scen, wid)) >= 1)


def check_banks(ch: tw.Pairs, scen: sg.Sg, prof: dict) -> None:
    us, victim, witness = players3(scen)
    v, w = tw.bank_parts(victim["BANK"]), tw.bank_parts(witness["BANK"])
    ch.eq("казна жертвы: шесть различных чисел", len(set(v)), 6)
    ch.is_true("казна жертвы: все шесть не нули", all(x > 0 for x in v), str(v))
    ch.eq("числа свидетеля не совпадают с числами жертвы", sorted(set(v) & set(w)), [])
    ch.is_true("казна нашего игрока низкая", max(tw.bank_parts(us["BANK"])) <= LOW_BANK, us["BANK"])
    for r in expectation_rows(scen):
        ch.is_true("%s: сумма с запасом ниже 9999" % r["ресурс"], r["сырое"] < fp.GOLD_CAP,
                   str(r["сырое"]))
        ch.is_true("%s: «всё» и «ничего» различаются" % r["ресурс"], r["всё"] != r["ничего"])
    for key, p in (("bank", us), ("victim_bank", victim), ("witness_bank", witness)):
        ch.eq("казна %s" % key, p["BANK"], prof[key])


def check_party(ch: tw.Pairs, t: sq.Tables, scen: sg.Sg, prof: dict) -> None:
    us, victim, _ = players3(scen)
    mine = tw.field_stacks(scen, us["PLAYER_ID"])
    ch.eq("наших отрядов на карте", len(mine), 1)
    if not mine:
        return
    _, s = mine[0]
    e = tw.entrance(tw.capital_xy(scen, victim["PLAYER_ID"]), tw.CAPITAL_SIZE)
    lead = t.unit(mp.unit_types(scen)[s["LEADER_ID"].upper()])
    need = m15.turns_to_walk(int(lead["MOVE"]), max(1, fp.cheb(tw.xy_of(s), e)))
    ch.is_true("до входа столицы %s за %d ход" % (e, prof["reach_turns"]),
               need <= prof["reach_turns"], "нужно ходов %d" % need)
    grid = sg.tile_grid(scen)
    ch.is_true("вход столицы %s проходим" % (e,), sg.passable(grid[e[1]][e[0]]))
    ours = tw.stack_units(t, scen, s)
    ch.is_true("солдат не больше лидерства", len(ours) - 1 <= int(lead["LEADERSHIP"] or 0))
    check_capture(ch, t, ours, garrison(t, scen, victim["PLAYER_ID"]),
                  capital_group(t, scen, victim["PLAYER_ID"], VICTIM), VICTIM)
    live = m20_rounds(t)
    ch.is_true("калибровка: модель на составе M20 не оптимистичнее живых %d раундов" % LIVE_M20[1],
               live is not None and live >= LIVE_M20[1], "модель %s" % live)


def check_cells(ch: tw.Pairs, scen: sg.Sg) -> None:
    """Клетки дописанных отрядов (наш и свидетеля): проходимы и без чужого объекта в плане."""
    us, _, witness = players3(scen)
    grid = sg.tile_grid(scen)
    _, plan = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    for pid in (us["PLAYER_ID"], witness["PLAYER_ID"]):
        for rec, s in tw.field_stacks(scen, pid):
            x, y = tw.xy_of(s)
            ch.is_true("клетка отряда %s проходима" % ((x, y),), sg.passable(grid[y][x]))
            others = [e for px, py, e in plan if (px, py) == (x, y) and e != rec.obj_id]
            ch.eq("чужих объектов на клетке отряда %s" % ((x, y),), others, [])


def check_safety(ch: tw.Pairs, t: sq.Tables, scen: sg.Sg) -> None:
    """Свидетель не дотягивается до нас: по равнине за `AI_TURNS` и по дороге за один ход.
    Жертва на карте пуста; нейтралов у места захвата нет."""
    us, _, witness = players3(scen)
    mine = [tw.xy_of(s) for _, s in tw.field_stacks(scen, us["PLAYER_ID"])]
    spot = fp.capital_tiles_at(tw.capital_xy(scen, witness["PLAYER_ID"]))
    theirs = [tw.xy_of(s) for _, s in tw.field_stacks(scen, witness["PLAYER_ID"])]
    for turns, factor in ((AI_TURNS, 1), (1, ROAD_FACTOR)):
        threats = m15.enemy_threats(t, scen, {"race": WITNESS, "turns": turns})
        ch.is_true("модель угрозы свидетеля не пуста", bool(threats))
        cover = max((n * per * factor for _, n, per in threats), default=0)
        for xy in mine:
            gap = min(fp.cheb(xy, c) for c in spot + theirs)
            ch.is_true("отряд %s вне досягаемости свидетеля (%d ход., шаг x%d)" % (xy, turns, factor),
                       gap > cover, "%d клеток при покрытии %d" % (gap, cover))
    near = [tw.xy_of(s) for _, s in tw.field_stacks(scen, neutral_id(scen))
            if any(fp.cheb(tw.xy_of(s), xy) <= CLEAR for xy in mine)]
    ch.eq("нейтральных отрядов ближе %d клеток" % CLEAR, near, [])


def run_checks(path: Path, t: sq.Tables, prof: dict = PROFILE, src: Path = SRC) -> tw.Pairs:
    """Все сравнения чтением файла; итог не подводится — это делает `verify` или selftest."""
    scen, base = sg.Sg.read(path), sg.Sg.read(src)
    ch = tw.Pairs("проверка чтением %s" % prof["file"])
    ch.eq("играбельных игроков", len(playable(scen)), PLAYABLE)
    if len(playable(scen)) != PLAYABLE:
        return ch
    check_common(ch, scen, prof)
    check_vanilla(ch, scen, base)
    check_dangling(ch, scen, base)
    check_no_end(ch, scen)
    check_banks(ch, scen, prof)
    check_party(ch, t, scen, prof)
    check_cells(ch, scen)
    check_safety(ch, t, scen)
    return ch


def verify(path: Path, t: sq.Tables, quiet: bool = False, prof: dict = PROFILE,
           src: Path = SRC) -> None:
    run_checks(path, t, prof, src).done(quiet)


# ------------------------------------------------------------------ ожидания

def expectation_rows(scen: sg.Sg) -> list[dict]:
    """По ресурсу: наша казна к бою (казна + тик при загрузке), казна жертвы без тика и с тиком,
    итог по трём гипотезам сразу после боя; «+ход» — наш дневной тик к концу хода."""
    us, victim, _ = players3(scen)
    inc = tw.bank_parts(fp.grace(US)["INCOME"])
    h = [a + b for a, b in zip(tw.bank_parts(us["BANK"]), inc)]
    n0 = tw.bank_parts(victim["BANK"])
    n1 = [a + b for a, b in zip(n0, tw.bank_parts(fp.grace(VICTIM)["INCOME"]))]
    return [{"ресурс": name, "до боя": h[k], "жертва": n0[k], "жертва+тик": n1[k],
             "всё": min(fp.GOLD_CAP, h[k] + n0[k]), "всё+тик": min(fp.GOLD_CAP, h[k] + n1[k]),
             "золото": h[k] + n0[k] if k == 0 else h[k], "ничего": h[k], "ход": inc[k],
             "сырое": h[k] + n1[k] + inc[k]} for k, name in enumerate(RESOURCES)]


def print_plan(t: sq.Tables, scen: sg.Sg, prof: dict = PROFILE) -> None:
    us, victim, witness = players3(scen)
    print("%s -> %s; казна: мы %s, жертва %s, свидетель %s"
          % (KEY, prof["name"], us["BANK"], victim["BANK"], witness["BANK"]))
    s = tw.field_stacks(scen, us["PLAYER_ID"])[0][1]
    ours = tw.stack_units(t, scen, s)
    gar = garrison(t, scen, victim["PLAYER_ID"])
    inner = capital_group(t, scen, victim["PLAYER_ID"], VICTIM)
    print("   отряд %s: %s; гарнизон жертвы %s, в столице %s, укрепление %d"
          % (tw.xy_of(s), [t.name(u) for u, _, _ in ours], [t.name(u) for u in gar],
             [t.name(u) for u in inner], tw.capital_floor(VICTIM)))
    for title, b in capture(t, ours, gar, inner, VICTIM):
        print("   бой «%s»: раундов %s, урон по защитнику за раунд %s, худший входящий %s"
              % (title, b["rounds"], {t.name(k): round(v) for k, v in b["damage"].items()},
                 b["incoming"][0]))
    print("   калибровка на M20: модель %s раундов, живьём %d" % (m20_rounds(t), LIVE_M20[1]))
    print("   %-15s %6s %6s %6s | %9s %9s | %9s %9s | %9s %9s"
          % ("ресурс", "до боя", "жертва", "+тик", "всё:бой", "всё:ход", "зол:бой", "зол:ход",
             "нич:бой", "нич:ход"))
    for r in expectation_rows(scen):
        print("   %-15s %6d %6d %6d | %9d %9d | %9d %9d | %9d %9d"
              % (r["ресурс"], r["до боя"], r["жертва"], r["жертва+тик"], r["всё"],
                 r["всё"] + r["ход"], r["золото"], r["золото"] + r["ход"], r["ничего"],
                 r["ничего"] + r["ход"]))


# ---------------------------------------------------------------------- selftest

def _field_rec(scen: sg.Sg, pid: str) -> sg.Record:
    return tw.field_stacks(scen, pid)[0][0]


def sabotage_players(scen: sg.Sg, what: str, t: sq.Tables) -> sg.Sg | None:
    """Порча игроков и состава карты; None — не эта группа."""
    us, victim, witness = players3(scen)
    vid, wid = victim["PLAYER_ID"], witness["PLAYER_ID"]
    race = sp.race_number(WITNESS)
    if what == "fewplayers":
        return replace(scen, records=tuple(r for r in scen.records if r.obj_id != wid))
    if what == "noroster":
        return sp.set_roster(scen, [sp.ROSTER_EMPTY if v == race else v for v in sp.roster(scen)])
    if what == "noheaderrace":
        return sp.set_header_races(scen, [v for v in sp.header_races(scen) if v != race])
    if what == "nocapstack":
        rec = tw.capital_of(scen, vid)
        sid = sg.fields(rec.body, sg.LAYOUT_CAPITAL)["STACK"]
        scen = tw.patch_capital(scen, rec, STACK=sg.NULL_ID)
        return drop_objects(scen, {sid})
    if what == "victimtown":
        rec = villages_of(scen, neutral_id(scen))[0]
        return scen.with_record(rec, replace(rec, body=tw._patch(rec.body, 0, sg.LAYOUT_CAPITAL,
                                                                 {"OWNER": vid})))
    if what == "victimstack":
        lead = fp.grace(VICTIM)["LEADER_1"].lower()
        return add_stacks(scen, [tw.party((60, 40), lead)], vid, t)
    if what == "witnessbare":
        return drop_objects(scen, stack_family(_field_rec(scen, wid)))
    if what == "witnessnear":
        return move_stack(scen, _field_rec(scen, wid), (9, 37))
    if what == "blocked":                    # отряд свидетеля внутри пятна своей столицы
        return move_stack(scen, _field_rec(scen, wid), (46, 64))
    if what == "neutralnear":
        return add_stacks(scen, [tw.party((9, 38), "g000uu5130")], neutral_id(scen), t)
    if what == "dangling":
        s = sg.parse_stack(_field_rec(scen, neutral_id(scen)).body)
        return replace(scen, records=tuple(r for r in scen.records if r.obj_id != s["UNIT_0"]))
    return None


def sabotage(scen: sg.Sg, what: str, t: sq.Tables) -> sg.Sg:
    got = sabotage_players(scen, what, t)
    if got is not None:
        return got
    us, victim, witness = players3(scen)
    banks = {"humanbank": (us["PLAYER_ID"], "G5000:R0000:Y0000:E0000:W0000:B0000"),
             "dupbank": (victim["PLAYER_ID"], "G0500:R0444:Y0444:E0555:W0666:B0777"),
             "clip": (victim["PLAYER_ID"], "G0500:R0333:Y9990:E0555:W0666:B0777"),
             "witnessbank": (witness["PLAYER_ID"], PROFILE["victim_bank"])}
    if what in banks:
        return mp.set_bank(scen, *banks[what])
    if what == "event":
        return scen.appended(sg.Sg.read(SRC).find(EVENT)[0])
    if what == "questlog":
        rec = scen.one(QUEST)
        return scen.with_record(rec, replace(rec, body=sg.f_count(rec.obj_id, 1)))
    if what == "m20party":
        scen = drop_objects(scen, stack_family(_field_rec(scen, us["PLAYER_ID"])))
        return add_stacks(scen, [tw.party(PARTY_XY, LEADER, (SOLDIER, SOLDIER), cell=1)],
                          us["PLAYER_ID"], t)
    if what == "far":
        return move_stack(scen, _field_rec(scen, us["PLAYER_ID"]), (24, 8))   # 29 клеток: 2 хода
    return tw.common_sabotage(scen, what, PROFILE, t)


# (порча, подпись, кусок строки ИМЕННО того сторожа, который обязан её поймать)
SABOTAGE = [("maxunit", "MAX_UNIT сбит на 2", "MAX_UNIT"),
            ("name", "имя карты чужое", "ScenarioInfo.NAME"),
            ("header", "имя в заголовке чужое", "имя в заголовке"),
            ("fewplayers", "играбельных игроков меньше трёх", "играбельных игроков"),
            ("noroster", "раса свидетеля не объявлена в ScenarioInfo", "ростер CScenarioInfo"),
            ("noheaderrace", "раса свидетеля не объявлена в заголовке", "таблица рас заголовка"),
            ("nocapstack", "у столицы жертвы пустой гарнизон", "гарнизоны столиц непусты"),
            ("victimtown", "у жертвы есть город", "городов у жертвы"),
            ("victimstack", "у жертвы есть отряд на карте", "отрядов жертвы на карте"),
            ("witnessbare", "у свидетеля ни отряда, ни города", "у свидетеля отряд на карте"),
            ("event", "событие сценария осталось", "событий сценария"),
            ("questlog", "журнал заданий не пуст", "журнала заданий QL"),
            ("humanbank", "казна игрока не та", "казна нашего игрока низкая"),
            ("dupbank", "две маны жертвы совпали", "шесть различных чисел"),
            ("clip", "сумма упрётся в 9999", "сумма с запасом ниже 9999"),
            ("witnessbank", "казна свидетеля как у жертвы", "числа свидетеля не совпадают"),
            ("m20party", "отряд M20: бой не выигрывается по расчёту", "переживает обе битвы"),
            ("far", "отряд за ход не дойдёт", "до входа столицы"),
            ("witnessnear", "свидетель рядом с нами", "вне досягаемости свидетеля"),
            ("blocked", "отряд свидетеля на занятой клетке", "чужих объектов на клетке"),
            ("neutralnear", "нейтрал у места захвата", "нейтральных отрядов ближе"),
            ("dangling", "ссылка на снятый юнит", "ссылок на снятые объекты")]


def failures(path: Path, t: sq.Tables) -> tuple[list[str], int]:
    """(все расхождения, всего сравнений); падение вспомогательной функции — одно расхождение."""
    msg = fp.raises(lambda: run_checks(path, t))
    if msg is not None:
        return [msg], 1
    ch = run_checks(path, t)
    return list(ch.bad), ch.good + len(ch.bad)


def selftest(scratch: Path) -> int:
    """Честная карта без расхождений; каждая порча ловится СВОИМ сторожем, а не чужим."""
    t = sq.Tables()
    scratch.mkdir(parents=True, exist_ok=True)
    out = scratch / ("%s_selftest.sg" % KEY)
    scen = build(t)
    scen.write(out)
    bad, total = failures(out, t)
    if bad or not total:
        raise SystemExit("честная карта %s не проходит проверку (сравнений %d): %s"
                         % (KEY, total, bad[:1]))
    missed = 0
    for what, title, expect in SABOTAGE:
        sabotage(scen, what, t).write(out)
        bad, _ = failures(out, t)
        mine = next((line for line in bad if expect in line), None)
        missed += mine is None
        print("%-44s %s" % (title, ("ловится: " + mine.split("\n")[0]) if mine else
                            "ПРОПУЩЕН, сработало другое: %s" % (bad[:1] or "ничего")))
    out.unlink()
    if not SABOTAGE:
        raise SystemExit("selftest %s: порч ноль — проверка ничего не доказала" % KEY)
    print("selftest %s: честная карта прошла (сравнений %d); своим сторожем поймано %d из %d "
          "порч, пропущено %d" % (KEY, total, len(SABOTAGE) - missed, len(SABOTAGE), missed))
    return 1 if missed else 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--out-dir", type=Path, default=mp.EXPORTS)
    p.add_argument("--plan", action="store_true", help="только расчёт, ничего не записывать")
    p.add_argument("--selftest", action="store_true", help="порча файла плюс сторож")
    p.add_argument("--scratch", type=Path,
                   default=Path(__file__).resolve().parents[1] / "corpus" / "_f7-selftest")
    a = p.parse_args(argv)
    if not SRC.exists():
        raise SystemExit("нет ванильной основы %s" % SRC)
    if a.selftest:
        return selftest(a.scratch)
    t = sq.Tables()
    scen = build(t)
    print_plan(t, scen)
    if a.plan:
        return 0
    out = a.out_dir / PROFILE["file"]
    scen.write(out)
    print("%s: записей %d" % (out, len(scen.records)))
    verify(out, t)
    return 0


if __name__ == "__main__":
    sys.exit(main())
