#!/usr/bin/env python3
"""Общая часть карт M18…M20 — все три собираются ЗАПЛАТОЙ поверх карты пользователя.

Эталон — `game-dev/Exports-archive/2-capital-test.sg` (ScenEdit, 121 запись, две столицы
с гарнизонами, раса соседа объявлена в обоих ростерах). Он открывается только на чтение;
почему не свой генератор второго игрока — шапка `make_f7_m15_map.py`.

Здесь: правка полей записей на месте, запись жезла, пересбор плана с жезлами, общие сторожа
(имя, потолки, ростеры, план, досягаемость соседа), расчёт боя и прогон `--selftest`.
Карты: `make_f7_m18_map.py` (мораль города), `make_f7_m19_map.py` (атака при мире и чужой
жезл), `make_f7_m20_map.py` (переход маны при захвате столицы). Игра отсюда не запускается.

**Бой считается грубо и в опасную сторону.** Наш урон — без случайной добавки и с точностью
как множителем; урон врага — с наибольшей добавкой `rng(BAT_DAMAGE)`, без промахов, и весь
сразу по одному нашему юниту. Броня защитника в укреплении — пол `max(своя, укрепление)`
(`Formulas.cs`, `computeArmor 0x5d2c8f`; живьём: страж столицы `S073UN0000`, `armor ret 50`
при `fort 50`, `corpus/20260911-passive`). Страж расы (`Grace.GUARDIAN`) в сценарии не
записан, но в бой выходит: там же `battle_units` содержит `G000UU3001`, которого в
`Power to Corrupt.sg` нет, — поэтому он всегда в списке защитников столицы, с полными 900 HP
из `Gunits` (живой страж показал `hp_max 190`; берём худшее).
"""
from __future__ import annotations

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
import sg_second_player as sp  # noqa: E402
from dbf_dump import read as dbf_read  # noqa: E402

SRC = m15.SRC
NEIGHBOR_RACE = "g000rr0001"        # Горные кланы — сосед эталона, столица (26,26)
WHAT_ROD = ".?AVCMidRod@@"
CAPITAL_SIZE = 5
# Прогон кончается первым ходом, до хода ИИ. Один ход ИИ — запас на случай, если прогон
# перетечёт на второй: столько ходов соседу и отпускает сторож досягаемости.
AI_TURNS = 1
LIMITS = (("MAX_UNIT", fp.MAX_UNIT_WANTED), ("MAX_LEADER", fp.MAX_LEADER_WANTED),
          ("MAX_SPELL", fp.MAX_SPELL_WANTED))
FRONT = (0, 2, 4)
MAX_DAMAGE, LEADER_BONUS = 300, 100          # computeDamageMax 0x61db98
LEADER_IDS = {0x0019, 0x0020, 0x0044, 0x0045, 0x0047, 0x0070, 0x0071, 0x0096, 0x8009, 0x8011}
DAMAGE_CLASSES = (1, 2, 15)                   # Damage, Drain, DrainOverflow
ARMOR_CAP = 90


class Pairs(city.Checks):
    """Счётчик «сошлось / разошлось»; падает и на расхождении, и на нуле сравнений."""

    def done(self, quiet: bool = False) -> None:
        total = self.good + len(self.bad)
        if not total:
            raise SystemExit("%s: сравнений ноль — проверка ничего не доказала" % self.title)
        if self.bad:
            raise SystemExit("%s: разошлось %d из %d — %s"
                             % (self.title, len(self.bad), total, self.bad[0]))
        if not quiet:
            print("%s: сошлось %d, разошлось 0 (сравнений %d)" % (self.title, total, total))

    def guard(self, what: str, fn) -> None:
        """Чужой сторож, который роняет сборку, — одним сравнением."""
        msg = fp.raises(fn)
        self.is_true(what, msg is None, (msg or "").split("\n")[0])


def party(xy: tuple[int, int], leader: str, units: tuple[str, ...] = (), cell: int = 0,
          why: str = "", exempt: tuple[str, ...] = ()) -> dict:
    """Отряд на карте в формате `make_f7_patch_maps.add_party`. `exempt` — от каких угроз
    отряд освобождён нарочно: `capital` — стоит у чужой столицы, `stack` — у чужого отряда."""
    return {"xy": xy, "leader": leader, "cat": None, "units": [(u, None) for u in units],
            "cell": cell, "why": why, "fights": False, "exempt": exempt}


# ------------------------------------------------------------------------ чтение

def players(scen: sg.Sg) -> tuple[dict, dict, dict]:
    """(человек, сосед, нейтрал): человек и сосед ищутся по расе, не по порядку записей."""
    human, neutral = mp.players(scen)
    neighbor = sp.second_player(scen, {"race": NEIGHBOR_RACE})
    if neighbor is None:
        raise SystemExit("в файле нет игрока расы %s — эталон не тот" % NEIGHBOR_RACE)
    return human, neighbor, neutral


def capital_of(scen: sg.Sg, player_id: str) -> sg.Record:
    return m15.capital_of(scen, player_id)


def capital_xy(scen: sg.Sg, player_id: str) -> tuple[int, int]:
    return sp.city_xy(capital_of(scen, player_id))


def entrance(xy: tuple[int, int], size: int) -> tuple[int, int]:
    """Вход укрепления — правый нижний угол пятна (`getFortOrRuinEntrance 0x4cd253`)."""
    return xy[0] + size - 1, xy[1] + size - 1


def xy_of(stack: dict) -> tuple[int, int]:
    return int(stack["POS_X"]), int(stack["POS_Y"])


def field_stacks(scen: sg.Sg, owner: str) -> list[tuple[sg.Record, dict]]:
    return [(r, s) for r in scen.find(sg.WHAT_STACK) for s in [sg.parse_stack(r.body)]
            if s["OWNER"] == owner and s["INSIDE"] == sg.NULL_ID]


def stack_at(scen: sg.Sg, owner: str, xy: tuple[int, int]) -> tuple[sg.Record, dict] | None:
    return next(((r, s) for r, s in field_stacks(scen, owner) if xy_of(s) == tuple(xy)), None)


def leader_cat(t: sq.Tables, scen: sg.Sg, stack: dict) -> int:
    return m15.leader_cat(t, mp.unit_types(scen), stack)


def stack_units(t: sq.Tables, scen: sg.Sg, stack: dict) -> list[tuple[str, bool, int]]:
    """(тип, стоит ли в переднем ряду, HP) каждого юнита отряда — по записи файла."""
    types = mp.unit_types(scen)
    units = {r.obj_id.upper(): r for r in scen.find(sg.WHAT_UNIT)}
    out = []
    for k in range(6):
        uid = stack["UNIT_%d" % k]
        if uid == sg.NULL_ID:
            continue
        cells = [c for c in range(6) if int(stack["POS_%d" % c]) == k]
        out.append((types[uid.upper()], any(c in FRONT for c in cells),
                    mp.unit_hp(units[uid.upper()])))
    return out


def rods(scen: sg.Sg) -> list[dict]:
    return [dict(sg.fields(r.body, "ROD_ID:s OWNER:s POS_X:i POS_Y:i"), rec=r)
            for r in scen.find(WHAT_ROD)]


def bank_parts(bank: str) -> list[int]:
    """`G0100:R0000:…` в шесть чисел; у `Grace.INCOME` пятая мана бывает опущена."""
    vals = [int(p[1:]) for p in bank.split(":")]
    return vals + [0] * (6 - len(vals))


# ------------------------------------------------------------------------ запись

def _patch(body: bytes, start: int, layout: str, vals: dict) -> bytes:
    """Переписать поля `vals` раскладки `layout` от смещения `start`; прочее байт в байт."""
    names = {i.rsplit(":", 1)[0] for i in layout.split()}
    if set(vals) - names:
        raise SystemExit("полей %s нет в раскладке" % sorted(set(vals) - names))
    c = sg.Cursor(body, start)
    out, last = [body[:start]], start
    for item in layout.split():
        name, kind = item.rsplit(":", 1)
        begin = c.pos
        {"s": c.str, "i": c.int, "b": c.bool}[kind](name)
        if name in vals:
            maker = {"s": sg.f_str, "i": sg.f_int, "b": sg.f_bool}[kind]
            out += [body[last:begin], maker(name, vals[name])]
            last = c.pos
    return b"".join(out) + body[last:]


def patch_stack(scen: sg.Sg, rec: sg.Record, **vals) -> sg.Sg:
    c = sg.Cursor(rec.body)
    c.fields(sg._GROUP)
    for _ in range(c.count()):
        c.str("ITEM_ID")
    body = _patch(rec.body, c.pos, sg._STACK_TAIL, vals)
    return scen.with_record(rec, replace(rec, body=body))


def patch_capital(scen: sg.Sg, rec: sg.Record, **vals) -> sg.Sg:
    return scen.with_record(rec, replace(rec, body=_patch(rec.body, 0, sg.LAYOUT_CAPITAL, vals)))


def add_rod(scen: sg.Sg, owner: str, xy: tuple[int, int]) -> sg.Sg:
    """`CMidRod` — раскладка снята с живых сохранений M15 (`S143RD0000`), четыре поля."""
    rid = scen.next_ids("RD")[0]
    body = (sg.f_str("ROD_ID", rid) + sg.f_str("OWNER", owner)
            + sg.f_int("POS_X", xy[0]) + sg.f_int("POS_Y", xy[1]))
    return scen.appended(sg.Record(WHAT_ROD, rid, body))


def rebuild_plan(scen: sg.Sg) -> sg.Sg:
    """Общий пересбор плана жезлов не знает — в сохранениях M15 жезл стоит в плане клеткой."""
    scen = mp.rebuild_plan(scen)
    return sg.add_plan_entries(scen, [(r["POS_X"], r["POS_Y"], r["ROD_ID"]) for r in rods(scen)])


def add_parties(scen: sg.Sg, parties: list[dict], owner: str, t: sq.Tables) -> sg.Sg:
    subrace = str(sg.fields(capital_of(scen, owner).body, sg.LAYOUT_CAPITAL)["SUBRACE"])
    for p in parties:
        scen = mp.add_party(scen, p, owner, subrace, t)
    return scen


def base(src: Path, prof: dict) -> sg.Sg:
    """Эталон с потолками, казной игрока и лордом; имя и план ставит `finish`."""
    scen = sg.Sg.read(src)
    human, _, _ = players(scen)
    for field, value in LIMITS:
        scen = city.set_info_int(scen, field, value)
    scen = mp.set_bank(scen, human["PLAYER_ID"], prof["bank"])
    if prof.get("lord"):
        scen = m15.set_lord(scen, human["PLAYER_ID"], prof["lord"])
    return scen


def finish(scen: sg.Sg, prof: dict) -> sg.Sg:
    return sg.set_scenario_name(rebuild_plan(scen), prof["name"])


# ------------------------------------------------------------------ общие сторожа

def check_common(ch: Pairs, t: sq.Tables, scen: sg.Sg, prof: dict) -> None:
    """Имя в обоих местах, потолки, казна, ростеры рас, записи игроков, план, лорд."""
    key = prof["key"]
    info = city.info_fields(scen)
    ch.eq("MAX_UNIT", int(info["MAX_UNIT"]), fp.MAX_UNIT_WANTED)
    ch.guard("потолки сценария", lambda: fp.check_scenario_limits(info, key))
    ch.eq("ScenarioInfo.NAME", sg.scenario_name(scen), prof["name"])
    raw = sg.header_name(scen)
    ch.eq("имя в заголовке", raw, prof["name"])
    human, _, _ = players(scen)
    ch.eq("казна игрока", human["BANK"], prof["bank"])
    ch.is_true("казна игрока ниже 9999", max(bank_parts(human["BANK"])) < fp.GOLD_CAP,
               human["BANK"])
    playable = [p for p in sp.players_of(scen) if p["RACE_ID"] != sc.RACE_NEUTRAL]
    ch.eq("играбельных игроков", len(playable), 2)
    ch.guard("раса соседа в обоих ростерах", lambda: sp._check_rosters(scen, key, playable))
    ch.guard("по записи тумана/заклинаний/построек на игрока", lambda: sp._check_records(scen, key))
    ch.guard("все отряды в плане", lambda: m15.check_plan(scen, prof, key))
    _, plan = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    ids = {r.obj_id for r in scen.records}
    ch.eq("висячих ссылок в плане", len({e for _, _, e in plan} - ids), 0)
    for r in rods(scen):
        ch.is_true("жезл %s в плане" % r["ROD_ID"],
                   (r["POS_X"], r["POS_Y"], r["ROD_ID"]) in set(plan))
    if prof.get("lord"):
        ch.guard("лорд — Повелитель Воров (CATEGORY 2)", lambda: m15.check_lord(scen, prof, key))


def check_garrisons(ch: Pairs, scen: sg.Sg, key: str) -> None:
    ch.guard("гарнизоны обеих столиц непусты и помечены INSIDE",
             lambda: sp._check_capitals(scen, key, {"race": NEIGHBOR_RACE}))


def check_safety(ch: Pairs, t: sq.Tables, scen: sg.Sg, prof: dict) -> None:
    """Сосед за `AI_TURNS` ходов не дотягивается до наших отрядов: из столицы — наймом и
    гарнизоном (модель M15), с карты — каждым своим отрядом по его очкам хода."""
    human, neighbor, _ = players(scen)
    cover, who = m15.enemy_cover(t, scen, {"race": NEIGHBOR_RACE, "turns": AI_TURNS})
    spot = fp.capital_tiles_at(capital_xy(scen, neighbor["PLAYER_ID"]))
    exempt = {tuple(p["xy"]): set(p.get("exempt", ())) for p in prof["parties"]}
    types = mp.unit_types(scen)
    mine = [xy_of(s) for _, s in field_stacks(scen, human["PLAYER_ID"])]
    foes = [(xy_of(s), m15.steps_per_turn(t.unit(types[s["LEADER_ID"].upper()])["MOVE"]))
            for _, s in field_stacks(scen, neighbor["PLAYER_ID"])]
    for xy in mine:
        free = exempt.get(xy, set())
        if "capital" not in free:
            gap = min(fp.cheb(xy, c) for c in spot)
            ch.is_true("отряд %s вне досягаемости чужой столицы" % (xy,), gap > cover,
                       "%d клеток при покрытии %d (%s)" % (gap, cover, who))
        for fxy, per in foes:
            if "stack" not in free:
                ch.is_true("отряд %s вне досягаемости чужого отряда %s" % (xy, fxy),
                           fp.cheb(xy, fxy) > per * AI_TURNS,
                           "%d клеток при %d за ход" % (fp.cheb(xy, fxy), per))


# ------------------------------------------------------------------------ бой

def _attack(t: sq.Tables, unit: str) -> dict | None:
    return t.attacks.get(str(t.unit(unit)["ATTACK_ID"]).lower())


def hit(t: sq.Tables, unit: str, worst: bool) -> int:
    """Урон одного удара до брони: `min(QTY_DAM, потолок)` плюс, в худшем случае, `BAT_DAMAGE−1`."""
    a = _attack(t, unit)
    if not a or int(a["CLASS"]) not in DAMAGE_CLASSES:
        return 0
    cap = MAX_DAMAGE + (LEADER_BONUS if int(unit[6:], 16) in LEADER_IDS else 0)
    base_dmg = min(int(a["QTY_DAM"] or 0), cap)
    return min(base_dmg + int(fp.gvars()["BAT_DAMAGE"]) - 1, cap) if worst else base_dmg


def cut(dwb: int, armor: int) -> int:
    """`dwb − dwb·armor/100` (`computeDamage 0x65b747`)."""
    return dwb - dwb * min(armor, ARMOR_CAP) // 100


def capital_floor(race: str) -> int:
    """Пол брони защитника столицы — `NUMBER` модификатора `Grace.PROTECTION` (TYPE 5)."""
    mod = fp.grace(race)["PROTECTION"].lower()
    rows = [r for r in dbf_read(sq.GLOBALS / "GmodifL.dbf")["rows"]
            if r["BELONGS_TO"].lower() == mod and int(r["TYPE"]) == 5]
    if len(rows) != 1:
        raise SystemExit("у модификатора %s %d строк брони, ожидалась одна" % (mod, len(rows)))
    return int(rows[0]["NUMBER"])


def battle(t: sq.Tables, ours: list[tuple[str, bool, int]], foes: list[str],
           floor: int) -> dict:
    """Раунды до победы и худший входящий урон за раунд на каждого нашего."""
    armor = {f: max(int(t.unit(f)["ARMOR"] or 0), floor) for f in foes}
    hp = {f: int(t.unit(f)["HIT_POINT"]) for f in foes}
    strikers = [u for u, front, _ in ours if front or not t.is_melee(u)]
    power = {u: int((_attack(t, u) or {"POWER": 0})["POWER"]) / 100.0 for u in strikers}
    aoe = [u for u in strikers if int((_attack(t, u) or {"REACH": 0})["REACH"]) == 1]
    single = [u for u in strikers if u not in aoe]
    # площадные бьют каждого защитника, одиночные — всем скопом того, кто сейчас на очереди
    dmg = {f: sum(cut(hit(t, u, False), armor[f]) * power[u] for u in strikers) for f in foes}
    if any(v <= 0 for v in dmg.values()):
        rounds = None
    elif single:                               # одиночные бьют по очереди: время складывается
        rounds = sum(math.ceil(hp[f] / dmg[f]) for f in foes)
    else:                                      # площадные бьют всех разом
        rounds = max(math.ceil(hp[f] / dmg[f]) for f in foes)
    incoming = [sum(cut(hit(t, f, True), int(t.unit(u)["ARMOR"] or 0)) for f in foes)
                for u, _, _ in ours]
    return {"rounds": rounds, "incoming": incoming, "damage": dmg, "armor": armor}


def check_battle(ch: Pairs, t: sq.Tables, title: str, ours: list[tuple[str, bool, int]],
                 foes: list[str], floor: int) -> dict:
    """Отряд побеждает, и каждый наш юнит переживает все раунды с запасом `HP_MARGIN`."""
    ch.is_true("%s: у отряда есть юниты" % title, bool(ours))
    ch.is_true("%s: есть с кем драться" % title, bool(foes))
    b = battle(t, ours, foes, floor)
    ch.is_true("%s: бой кончается" % title, b["rounds"] is not None,
               "кому-то из защитников урон ноль: %s" % b["damage"])
    for (unit, _, hp), inc in zip(ours, b["incoming"]):
        room = hp * (1.0 - fp.HP_MARGIN)
        ch.is_true("%s: %s переживает бой" % (title, t.name(unit)),
                   b["rounds"] is not None and b["rounds"] * inc < room,
                   "%s раундов x %d урона = %s при %d HP (запас %.0f %%)"
                   % (b["rounds"], inc, b["rounds"] and b["rounds"] * inc, hp,
                      100 * fp.HP_MARGIN))
    return b


# ---------------------------------------------------------------------- selftest

def header_sabotage(scen: sg.Sg, prof: dict) -> sg.Sg:
    """Имя в заголовке чужое, в `ScenarioInfo` — прежнее."""
    at = sg.header_name_offset(scen.header)
    return replace(scen, header=scen.header[:at] + b"chuzhoe".ljust(256, b"\x00")
                   + scen.header[at + 256:])


COMMON_SABOTAGE = [("maxunit", "MAX_UNIT сбит на 2"), ("name", "имя карты чужое"),
                   ("header", "имя в заголовке чужое"),
                   ("noroster", "раса соседа не объявлена в ScenarioInfo"),
                   ("noheaderrace", "раса соседа не объявлена в заголовке")]


def common_sabotage(scen: sg.Sg, what: str, prof: dict, t: sq.Tables) -> sg.Sg:
    if what == "header":
        return header_sabotage(scen, prof)
    return mp.sabotage_map(scen, what, t)


def selftest(prof: dict, build_fn, verify_fn, sabotage_fn, cases: list[tuple[str, str]],
             scratch: Path) -> int:
    """Честная карта проходит проверку чтением; каждая порча ровно одного обещания — нет."""
    t = sq.Tables()
    scratch.mkdir(parents=True, exist_ok=True)
    out = scratch / ("%s_selftest.sg" % prof["key"])
    scen = build_fn(t)
    scen.write(out)
    honest = fp.raises(lambda: verify_fn(out, t, quiet=True))
    if honest is not None:
        raise SystemExit("честная карта %s не проходит собственную проверку: %s"
                         % (prof["key"], honest))
    got = []
    for what, title in cases:
        sabotage_fn(scen, what, t).write(out)
        got.append((title, fp.raises(lambda: verify_fn(out, t, quiet=True))))
    out.unlink()
    if not got:
        raise SystemExit("selftest %s не сделал ни одного сравнения" % prof["key"])
    bad = sum(msg is None for _, msg in got)
    for title, msg in got:
        print("%-52s %s" % (title, ("ловится: " + msg.split("\n")[0]) if msg else "ПРОПУЩЕН"))
    print("selftest %s: честная карта прошла; сработали %d сторожей из %d порч"
          % (prof["key"], len(got) - bad, len(got)))
    return 1 if bad else 0
