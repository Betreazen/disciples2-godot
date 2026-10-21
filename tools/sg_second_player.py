#!/usr/bin/env python3
"""Второй ИГРАБЕЛЬНЫЙ игрок в сценарии `.sg` — писатель и проверка.

Отдельный модуль, потому что `make_f7_patch_maps.py` иначе перевалил бы за восемьсот строк,
и потому что кусок самодостаточен: он не знает ни про профили карт Ф7, ни про их сборку.

**Зачем.** На одиночной карте шкалы отношений нет вовсе — записи `CMidDiplomacy` в прогоне
09.09.2026 не сдвинулись ни разу (`f7-map-economy-leaders.md` §10.7). Чтобы измерить
`GVars.D_ROD = −15` и `D_NOBLE = −5`, на карте обязан быть сосед другой расы.

**Что ему положено**, посчитано по 33 ванильным картам `game-dev/Exports-archive`: на каждого
игрока ровно по одной записи `CMidPlayer`, `CMidgardMapFog`, `CPlayerKnownSpells`,
`CPlayerBuildings` и `CMidSubRace`, а `CCapital` — по одной на каждого ИГРАБЕЛЬНОГО
(у нейтрала столицы нет). Пар в `CMidDiplomacy` — по числу неупорядоченных пар рас: при двух
играбельных плюс нейтрал их три.

**Записей мало — нужен ещё РОСТЕР (10.09.2026, разбор падения).** Первая версия писала только
объекты и получала `ACCESS_VIOLATION` на `0x0058AE84` (`Discipl2.exe.crash.txt`, 10.09.2026):
это геттер `mov eax,[ecx+8]` с `this = 0`, вызванный из `0x004AD3D5` (возврат `0x004AD427`) —
заполнение дерева построек в `DLG_BUILD_STRUCTURE`. Функция строит id постройки по формату
`"G000BB0%03x"` (`0x0079C7D4`) и берёт у найденной записи имя; номер оказался `0x9001`, такой
постройки в `Gbuild.dbf` нет (там ровно 122, `0001`…`0122`), поиск вернул ноль — и геттер
разыменовал ноль. Причина в том, что раса соседа не была ОБЪЯВЛЕНА:

* `CScenarioInfo.PLAYER_1…PLAYER_13` — ростер номеров рас (`Grace.RACE_TYPE`), пустые места
  забиты 99 (движок пишет их сам, `0x005E3EE9`: `mov byte [ebp-0xd], 0x63`). Ваниль:
  `Devils Night` = 4, 2, 3; `Three Temples` = 4, 0, 3, 5, 1. У нас было 4, 0, 99 — Горных
  кланов в ростере не было, хотя `CMidPlayer` для них лежал в файле.
* Таблица рас ЗАГОЛОВКА (`sg.ksy`: `total_races` + массив `race_info` по 40 байт). У нас
  стояло 2 расы вместо 3.

Ещё два расхождения с ванилью того же разбора: у соседа не было НИ ОДНОГО отряда, а у его
столицы `STACK` был пуст. В 33 ванильных картах нет ни одной столицы с пустым гарнизоном и ни
одного играбельного игрока без отряда; гарнизон столицы — всегда `Grace.LEADER_1` своей расы.

Игра отсюда не запускается.
"""
from __future__ import annotations

import functools
import struct
import sys
from dataclasses import replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402
import scenario_c4 as sc  # noqa: E402
import make_c4_map as c4  # noqa: E402   (GLOBALS — путь к ванильным .dbf)
import make_city_map as city  # noqa: E402  (info_layout — раскладка тела CScenarioInfo)
import f7_wave2 as w2  # noqa: E402         (grace — RACE_TYPE и играбельность из Grace.dbf)
from dbf_dump import read as dbf_read  # noqa: E402

WHAT_FOG = ".?AVCMidgardMapFog@@"
WHAT_KNOWN = ".?AVCPlayerKnownSpells@@"
WHAT_BUILDS = ".?AVCPlayerBuildings@@"
WHAT_DIPLO = ".?AVCMidDiplomacy@@"

# Ростер рас записи `CScenarioInfo`: тринадцать мест, свободное = 99.
ROSTER_SLOTS = 13
ROSTER_EMPTY = 99
_INFO_TO_ROSTER = " DIFFSCEN:i DIFFGAME:i CREATOR:s"

# Таблица рас заголовка (`reference/D2SGFormat/sg.ksy`, тип `d2ee_header`): после 250 значений
# ГПСЧ идут `padding_size`, `total_races` и массив `race_info` (u4 номер расы + 36 нулей).
HEADER_TOTAL_RACES = 0x0A84
HEADER_RACES = 0x0A88
RACE_INFO_LEN = 40
HEADER_SIZE = 0x0C          # u4: «размер заголовка» = len(header) − 0x10
HEADER_MAX = 2920           # потолок из кода игры (sg.ksy)


def players_of(scen: sg.Sg) -> list[dict]:
    return [sg.fields(r.body, sg.LAYOUT_PLAYER) for r in scen.find(sg.WHAT_PLAYER)]


def human_player(scen: sg.Sg) -> dict:
    """Игрок прогона ищется по РАСЕ, а не «первый не нейтральный»: их бывает двое."""
    return next(p for p in players_of(scen)
                if p["RACE_ID"].upper() == sc.RACE_EMPIRE.upper())


def second_player(scen: sg.Sg, spec: dict) -> dict | None:
    return next((p for p in players_of(scen)
                 if p["RACE_ID"].upper() == spec["race"].upper()), None)


def race_number(race_id: str) -> int:
    """Номер расы в ростере и в дипломатии — это `Grace.RACE_TYPE`, а НЕ цифра `RACE_ID`."""
    return int(w2.grace(race_id)["RACE_TYPE"])


def city_xy(rec: sg.Record) -> tuple[int, int]:
    f = sg.fields(rec.body, sg.LAYOUT_CAPITAL)
    return int(f["POS_X"]), int(f["POS_Y"])


def subrace_body(sid: str, number: int, player_id: str) -> bytes:
    """Тело `CMidSubRace` — раскладка снята с десяти записей шаблона до последнего байта."""
    return (sg.f_str("SUBRACE_ID", sid) + sg.f_int("SUBRACE", number)
            + sg.f_str("PLAYER_ID", player_id) + sg.f_int("NUMBER", 0)
            + sg.f_str("NAME_TXT", "") + sg.f_int("BANNER", 0))


def capital_body(cid: str, name: str, owner: str, subrace: str, xy: tuple[int, int],
                 stack: str = sg.NULL_ID) -> bytes:
    """Тело `CCapital`: голова как у города, пустой внутренний список, пустой инвентарь.
    `stack` — гарнизон; в ванили он не пуст ни у одной из 33 карт."""
    return (sg.f_str("CITY_ID", cid) + sg.f_str("NAME_TXT", name) + sg.f_str("DESC_TXT", "")
            + sg.f_str("OWNER", owner) + sg.f_str("SUBRACE", subrace)
            + sg.f_str("STACK", stack)
            + sg.f_int("POS_X", xy[0]) + sg.f_int("POS_Y", xy[1])
            + sg.f_str("GROUP_ID", cid)
            + b"".join(sg.f_str("UNIT_%d" % k, sg.NULL_ID) for k in range(6))
            + b"".join(sg.f_int("POS_%d" % k, -1) for k in range(6))
            + sg.f_count(cid, 0) + sg.f_int("AIPRIORITY", 3))


# ------------------------------------------------- ростер рас: ScenarioInfo и заголовок

def _roster_start(scen: sg.Sg) -> tuple[sg.Record, int]:
    """Запись `CScenarioInfo` и смещение поля `PLAYER_1` в её теле."""
    rec = scen.one(sg.WHAT_INFO)
    c = sg.Cursor(rec.body)
    c.fields(city.info_layout(scen) + _INFO_TO_ROSTER)
    return rec, c.pos


def roster(scen: sg.Sg) -> list[int]:
    """`PLAYER_1…PLAYER_13` — номера рас сценария; 99 значит «места нет»."""
    rec, start = _roster_start(scen)
    c = sg.Cursor(rec.body, start)
    return [c.int("PLAYER_%d" % (k + 1)) for k in range(ROSTER_SLOTS)]


def set_roster(scen: sg.Sg, slots: list[int]) -> sg.Sg:
    """Переписать ростер целиком; длина тела не меняется — все места целые."""
    if len(slots) != ROSTER_SLOTS:
        raise SystemExit("в ростере ScenarioInfo ровно %d мест, дано %d"
                         % (ROSTER_SLOTS, len(slots)))
    rec, start = _roster_start(scen)
    c = sg.Cursor(rec.body, start)
    for k in range(ROSTER_SLOTS):
        c.int("PLAYER_%d" % (k + 1))
    body = rec.body[:start] + b"".join(
        sg.f_int("PLAYER_%d" % (k + 1), v) for k, v in enumerate(slots)) + rec.body[c.pos:]
    return scen.with_record(rec, replace(rec, body=body))


def add_roster_race(scen: sg.Sg, race: int) -> sg.Sg:
    """Объявить расу в ростере `CScenarioInfo` — на первое свободное место."""
    slots = roster(scen)
    if race in slots:
        return scen
    if ROSTER_EMPTY not in slots:
        raise SystemExit("в ростере ScenarioInfo нет свободного места под расу %d" % race)
    slots[slots.index(ROSTER_EMPTY)] = race
    return set_roster(scen, slots)


def header_races(scen: sg.Sg) -> list[int]:
    """Таблица рас заголовка: её читает список сценариев в игре."""
    total = struct.unpack_from("<I", scen.header, HEADER_TOTAL_RACES)[0]
    return [struct.unpack_from("<I", scen.header, HEADER_RACES + k * RACE_INFO_LEN)[0]
            for k in range(total)]


def set_header_races(scen: sg.Sg, races: list[int]) -> sg.Sg:
    """Переписать таблицу рас заголовка: счётчик, блоки по 40 байт и поле размера."""
    old = struct.unpack_from("<I", scen.header, HEADER_TOTAL_RACES)[0]
    tail = scen.header[HEADER_RACES + old * RACE_INFO_LEN:]
    block = b"".join(struct.pack("<I", r) + b"\0" * (RACE_INFO_LEN - 4) for r in races)
    head = scen.header[:HEADER_RACES] + block + tail
    head = (head[:HEADER_TOTAL_RACES] + struct.pack("<I", len(races))
            + head[HEADER_TOTAL_RACES + 4:])
    head = head[:HEADER_SIZE] + struct.pack("<I", len(head) - 0x10) + head[HEADER_SIZE + 4:]
    if len(head) > HEADER_MAX:
        raise SystemExit("заголовок вырос до %d байт, потолок кода игры %d"
                         % (len(head), HEADER_MAX))
    return replace(scen, header=head)


def add_header_race(scen: sg.Sg, race: int) -> sg.Sg:
    races = header_races(scen)
    return scen if race in races else set_header_races(scen, races + [race])


# ------------------------------------------------------------- гарнизон столицы

@functools.lru_cache(maxsize=None)
def leader_of(race_id: str) -> tuple[str, int, int]:
    """Тип, здоровье и ход лидера расы (`Grace.LEADER_1`). Именно он стоит в ванильных
    гарнизонах столиц: `Devils Night` и `Three Temples`, 6 столиц из 6."""
    type_id = w2.grace(race_id)["LEADER_1"].upper()
    row = next((r for r in dbf_read(c4.GLOBALS / "Gunits.dbf")["rows"]
                if r["UNIT_ID"].upper() == type_id), None)
    if row is None:
        raise SystemExit("лидер расы %s (%s) не найден в Gunits.dbf" % (race_id, type_id))
    return type_id, int(row["HIT_POINT"]), int(row["MOVE"])


def add_capital_garrison(scen: sg.Sg, spec: dict, pid: str, sub: str,
                         cid: str) -> tuple[sg.Sg, str]:
    """Гарнизон столицы: лидер расы в клетке 2, `INSIDE` — сама столица. Клетку плана отряду
    добавит общий пересбор плана (в ванили гарнизон стоит в плане на клетке столицы)."""
    type_id, hp, move = leader_of(spec["race"])
    uid = scen.next_ids("UN")[0]
    scen = scen.appended(sg.Record(sg.WHAT_UNIT, uid, sg.unit_body(uid, type_id, 1, hp)))
    sid = scen.next_ids("KC")[0]
    body = sg.stack_body(sid, [uid], [-1, -1, 0, -1, -1, -1], uid,
                         spec["capital"][0], spec["capital"][1], pid, sub,
                         move=move, inside=cid)
    return scen.appended(sg.Record(sg.WHAT_STACK, sid, body)), sid


# ------------------------------------------------------------------- дипломатия

def relation_word(current: int, previous: int | None = None, alliance: bool = False) -> int:
    """Упаковка `DiplomacyRelation.relation` (`diplomacyhooks.cpp:63,79,93`): бит 0 — союз,
    биты 1…7 — текущее отношение, биты 8…14 — прошлое. Ноль читается как ВОЙНА, поэтому
    ванильные пары с нейтралом и стоят нулями. `alliance=True` ставит бит 0."""
    previous = current if previous is None else previous
    return (current << 1) | (previous << 8) | int(alliance)


def set_diplomacy(scen: sg.Sg, pairs: list[tuple[int, int, int]]) -> sg.Sg:
    rec = scen.one(WHAT_DIPLO)
    body = sg.f_count(rec.obj_id, len(pairs)) + b"".join(
        sg.f_int("RACE_1", a) + sg.f_int("RACE_2", b) + sg.f_int("RELATION", r)
        for a, b, r in pairs)
    return scen.with_record(rec, replace(rec, body=body))


def diplomacy_pairs(scen: sg.Sg, spec: dict) -> list[tuple[int, int, int]]:
    """Пары рас: нейтрал с каждым по нулю (война), а между расами игроков — заданное число.

    Номер расы в записи — это `Grace.RACE_TYPE`, а НЕ последняя цифра `RACE_ID`: у Горных
    кланов `rr0001` тип 3, и ванильные карты (`Devils Night`) пишут именно 3.
    """
    ours = race_number(sc.RACE_EMPIRE)
    theirs = race_number(spec["race"])
    neutral = race_number(sc.RACE_NEUTRAL)
    return [(neutral, ours, 0), (neutral, theirs, 0),
            (ours, theirs, relation_word(spec["relation"]))]


# --------------------------------------------------------------------- писатель

def add_second_player(scen: sg.Sg, spec: dict) -> tuple[sg.Sg, str, str]:
    """Второй ИГРАБЕЛЬНЫЙ игрок: он и есть условие существования шкалы отношений.

    Голова записи игрока пишется заново, хвост (`SPELL_BANK`…`EXMAP*`) копируется у человека
    БАЙТ В БАЙТ: у обоих игроков шаблона он совпадает, значит выдумывать там нечего. Дальше —
    подраса, столица со своим гарнизоном, дипломатия и ОБЪЯВЛЕНИЕ РАСЫ в двух ростерах
    (без него игра падает на `0x0058AE84`, см. шапку модуля).
    """
    human = human_player(scen)
    src = next(r for r in scen.find(sg.WHAT_PLAYER)
               if sg.fields(r.body, "PLAYER_ID:s")["PLAYER_ID"] == human["PLAYER_ID"])
    pid, fog = scen.next_ids("PL")[0], scen.next_ids("FG")[0]
    known, builds = scen.next_ids("KS")[0], scen.next_ids("PB")[0]
    c = sg.Cursor(src.body)
    c.fields(sg.LAYOUT_PLAYER)
    head = (sg.f_str("PLAYER_ID", pid) + sg.f_str("NAME_TXT", spec["name"])
            + sg.f_str("DESC_TXT", "") + sg.f_str("LORD_ID", spec["lord"].upper())
            + sg.f_str("RACE_ID", spec["race"].upper()) + sg.f_str("FOG_ID", fog)
            + sg.f_str("KNOWN_ID", known) + sg.f_str("BUILDS_ID", builds)
            + sg.f_int("FACE", 0) + sg.f_int("QTY_BREAKS", 0)
            + sg.f_str("BANK", spec["bank"]) + sg.f_bool("IS_HUMAN", 0))
    src_fog = scen.find(WHAT_FOG)[0]
    scen = scen.appended(
        sg.Record(sg.WHAT_PLAYER, pid, head + src.body[c.pos:]),
        sg.Record(WHAT_FOG, fog, fog.encode() + src_fog.body[10:]),
        sg.Record(WHAT_KNOWN, known, sg.f_count(known, 0)),
        sg.Record(WHAT_BUILDS, builds, sg.f_count(builds, 0)))
    sub = scen.next_ids("SR")[0]
    scen = scen.appended(sg.Record(sg.WHAT_SUBRACE, sub,
                                   subrace_body(sub, spec["subrace"], pid)))
    cid = scen.next_ids("FT")[0]            # id занимаем заранее: гарнизон ссылается на него
    scen, garrison = add_capital_garrison(scen, spec, pid, sub, cid)
    scen = scen.appended(sg.Record(sg.WHAT_CAPITAL, cid,
                                   capital_body(cid, spec["name"], pid, sub,
                                                spec["capital"], garrison)))
    race = race_number(spec["race"])
    scen = add_roster_race(add_header_race(scen, race), race)
    return set_diplomacy(scen, diplomacy_pairs(scen, spec)), pid, sub


# --------------------------------------------------------------------- сторожа

def _check_records(scen: sg.Sg, key: str) -> None:
    """По записи `CMidgardMapFog`, `CPlayerKnownSpells` и `CPlayerBuildings` на игрока."""
    want = len(scen.find(sg.WHAT_PLAYER))
    for what in (WHAT_FOG, WHAT_KNOWN, WHAT_BUILDS):
        got = len(scen.find(what))
        if got != want:
            raise SystemExit("%s: записей %s %d, а по ванили их ровно по одной на игрока (%d)"
                             % (key, what, got, want))


def _check_rosters(scen: sg.Sg, key: str, playable: list[dict]) -> None:
    """Раса каждого играбельного игрока объявлена и в `CScenarioInfo`, и в заголовке.

    Без этого игра падает в `DLG_BUILD_STRUCTURE` на `0x0058AE84`: дерево построек берётся
    по расе, а её в таблице нет — поиск постройки отдаёт ноль, геттер имени его разыменует.
    """
    want = sorted({race_number(p["RACE_ID"]) for p in playable}
                  | {race_number(sc.RACE_NEUTRAL)})
    got_roster = sorted(v for v in roster(scen) if v != ROSTER_EMPTY)
    if got_roster != want:
        raise SystemExit("%s: ростер CScenarioInfo.PLAYER_n = %s, ожидались расы %s "
                         "(без объявления раса не существует для движка)"
                         % (key, got_roster, want))
    got_header = sorted(header_races(scen))
    if got_header != want:
        raise SystemExit("%s: таблица рас заголовка = %s, ожидались расы %s"
                         % (key, got_header, want))


def _check_capitals(scen: sg.Sg, key: str, spec: dict) -> None:
    """У каждой столицы непустой гарнизон, а у соседа есть отряд: так во всех 33 ванильных."""
    stacks = {r.obj_id: sg.parse_stack(r.body) for r in scen.find(sg.WHAT_STACK)}
    for rec in scen.find(sg.WHAT_CAPITAL):
        f = sg.fields(rec.body, sg.LAYOUT_CAPITAL)
        if f["STACK"] == sg.NULL_ID or f["STACK"] not in stacks:
            raise SystemExit("%s: у столицы %s гарнизон %s — в ванили пустых нет ни одного"
                             % (key, rec.obj_id, f["STACK"]))
        if stacks[f["STACK"]]["INSIDE"] != rec.obj_id:
            raise SystemExit("%s: гарнизон %s столицы %s не помечен INSIDE"
                             % (key, f["STACK"], rec.obj_id))
    p = second_player(scen, spec)
    mine = [s for s in stacks.values() if s["OWNER"] == p["PLAYER_ID"]]
    if not mine:
        raise SystemExit("%s: у соседа расы %s нет ни одного отряда — в ванили у каждого "
                         "играбельного игрока их минимум один" % (key, spec["race"]))


def verify_second(scen: sg.Sg, prof: dict, key: str) -> None:
    """Сосед: игрок, его столица с гарнизоном, его подраса, ростеры рас и пары дипломатии."""
    spec = prof.get("second")
    playable = [p for p in players_of(scen) if p["RACE_ID"] != sc.RACE_NEUTRAL]
    caps = scen.find(sg.WHAT_CAPITAL)
    _check_rosters(scen, key, playable)
    if spec is None:
        if len(playable) != 1:
            raise SystemExit("%s: играбельных игроков %d, ожидался один" % (key, len(playable)))
        return
    p = second_player(scen, spec)
    if p is None or len(playable) != 2:
        raise SystemExit("%s: второго играбельного игрока расы %s в файле нет — без него шкалы "
                         "отношений не существует" % (key, spec["race"]))
    if p["BANK"] != spec["bank"]:
        raise SystemExit("%s: казна соседа %s, ожидалась %s" % (key, p["BANK"], spec["bank"]))
    _check_records(scen, key)
    mine = [r for r in caps if city_xy(r) == tuple(spec["capital"])]
    if len(caps) != 2 or not mine:
        raise SystemExit("%s: столиц %d, чужая в %s не найдена" % (key, len(caps), spec["capital"]))
    _check_capitals(scen, key, spec)
    want = diplomacy_pairs(scen, spec)
    c = sg.Cursor(scen.one(WHAT_DIPLO).body)
    got = [(c.int("RACE_1"), c.int("RACE_2"), c.int("RELATION")) for _ in range(c.count())]
    if sorted(got) != sorted(want):
        raise SystemExit("%s: пары дипломатии %s, ожидались %s" % (key, got, want))
