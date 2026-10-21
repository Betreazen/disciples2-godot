#!/usr/bin/env python3
"""Общая часть карт для среза экранов интерфейса (tools/ui-maps/<карта>/make_map.py).

Карты собираются из штатных сценариев game-pristine. Отряды с вещами на Mercantile — через
disciples-modding/docs/synthetic-maps/p3a_common, деревня эльфов и вор на Fardale's Web — через
p4a_common (оба только импортируются). Здесь — то, чего там нет:

  * событие сценария «отряд перестал существовать -> победа / поражение» (CMidEvent, условие 17,
    эффект 0; раскладка — sg.ksy disciples-mod, образцы — штатные Arrival of the Pilgrims, Last Stand);
  * руина, уже разведанная игроком (список посетителей CMidRuin): по коду 0x579510 правый щелчок по
    такой руине открывает DLG_R_C_RUIN, по неразведанной — DLG_R_C_MISC;
  * известные заклинания игрока (CPlayerKnownSpells) — без них «Украсть заклинание» не предлагается;
  * флаг SOURCE_M сценария (в файлах своих кампаний 0);
  * файл своей кампании .csg (контейнер MQDB: HEADER.CPN, ORDER.CPN и сценарии .sg);
  * запуск: --plan, --verify, --selftest, запись в out/ и --install (копия в game-dev только новым
    файлом; существующие файлы не перезаписываются).

Модуль нарочно не называется при импорте `common`: так называется каркас `_batch1a/common.py`,
который тянут p3a/p4a. Карты грузят его через `load()` под именем `ui_common`.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import shutil
import struct
import sys
from dataclasses import replace
from pathlib import Path

sys.dont_write_bytecode = True
sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]

PORT = Path(__file__).resolve().parents[2]                 # disciples-godot
MOD = PORT.parent / "disciples-modding"
SYNTH = MOD / "docs" / "synthetic-maps"
PRISTINE = MOD / "game-pristine" / "Exports"
GAME_EXPORTS = MOD / "game-dev" / "Exports"
GAME_CAMPAIGN = MOD / "game-dev" / "Campaign"             # папка Campaign из настроек игры (+0xC4)
for p in (PORT / "tools", SYNTH):
    if str(p) not in sys.path:
        sys.path.insert(0, str(p))
import sgfile as sg  # noqa: E402

EMPIRE, ELVES, NEUTRAL = "G000RR0000", "G000RR0005", "G000RR0004"
NOBODY = sg.NULL_ID
WHAT_EVENT = ".?AVCMidEvent@@"
WHAT_RUIN = ".?AVCMidRuin@@"
WHAT_KNOWN = ".?AVCPlayerKnownSpells@@"
RACE_FLAGS = ("HUMAN", "DWARF", "UNDEAD", "HERETIC", "NEUTRAL", "ELF")
COND_STACK_EXISTS, EFFECT_WIN_LOSE = 17, 0


def load():
    """Этот модуль под именем ui_common (см. шапку)."""
    return sys.modules[__name__]


# ------------------------------------------------------------------ счётчики

class Counter2:
    """Строки `имя сошлось=N разошлось=M`; каждая неудача печатается с причиной."""

    def __init__(self, quiet: bool = False) -> None:
        self.rows: dict[str, list[int]] = {}
        self.notes: list[str] = []
        self.quiet = quiet

    def count(self, name: str, ok: bool, what: str) -> None:
        row = self.rows.setdefault(name, [0, 0])
        row[0 if ok else 1] += 1
        if not ok:
            self.notes.append(f"  {name} РАЗОШЛОСЬ: {what}")

    def failed(self) -> set[str]:
        return {k for k, (_, bad) in self.rows.items() if bad}

    def empty(self) -> set[str]:
        return {k for k, (good, _) in self.rows.items() if good == 0}

    def print(self) -> None:
        if self.quiet:
            return
        for line in self.notes:
            print(line)
        for k, (ok, bad) in self.rows.items():
            print(f"{k} сошлось={ok} разошлось={bad}" + ("  (не закрыто: N=0)" if ok == 0 else ""))


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def record(scen: sg.Sg, obj_id: str) -> sg.Record:
    return next(r for r in scen.records if r.obj_id == obj_id)


def players(scen: sg.Sg) -> dict[str, dict]:
    """{RACE_ID: поля CMidPlayer}."""
    out = {}
    for r in scen.find(sg.WHAT_PLAYER):
        f = sg.fields(r.body, sg.LAYOUT_PLAYER)
        out[str(f["RACE_ID"]).upper()] = f
    return out


def has_elf_flags(scen: sg.Sg) -> bool:
    """Поля ELF/VERELF у событий есть только в файлах с подписью D2EESFISIG (sg.ksy)."""
    return scen.header.startswith(b"D2EESFISIG")


# ------------------------------------------------------------------ события

def cond_stack_gone(stack_id: str) -> bytes:
    """Условие 17 «отряд существует / нет»: MISC_INT 1 — отряда больше нет."""
    return sg.f_int("CATEGORY", COND_STACK_EXISTS) + sg.f_str("ID_STACK", stack_id) + sg.f_int("MISC_INT", 1)


def effect_end(win: bool, player: str, num: int = 0) -> bytes:
    """Эффект 0 «победа или поражение». Поражение у штатных карт — с игроком G000000000."""
    return (sg.f_int("CATEGORY", EFFECT_WIN_LOSE) + sg.f_int("NUM", num)
            + sg.f_bool("WIN_SCEN", 1 if win else 0) + sg.f_str("ID_PLAYER1", player))


def event_body(eid: str, name: str, affected: set[str], verify: set[str], order: int,
               conds: list[bytes], effects: list[bytes], elf: bool) -> bytes:
    flags = RACE_FLAGS if elf else RACE_FLAGS[:5]
    return (sg.f_str("ID", eid) + sg.f_str("NAME_TXT", name)
            + b"".join(sg.f_bool(f, int(f in affected)) for f in flags)
            + b"".join(sg.f_bool("VER" + f, int(f in verify)) for f in flags)
            + sg.f_bool("ENABLED", 1) + sg.f_bool("OCCUR_ONCE", 1)
            + sg.f_int("CHANCE", 100) + sg.f_int("ORDER", order)
            + sg.f_int("COND_QTY", len(conds)) + b"".join(conds)
            + sg.f_int("EFFECT_QTY", len(effects)) + b"".join(effects))


def event_head(body: bytes, elf: bool) -> dict:
    flags = RACE_FLAGS if elf else RACE_FLAGS[:5]
    layout = ("ID:s NAME_TXT:s " + " ".join(f"{f}:b" for f in flags) + " "
              + " ".join(f"VER{f}:b" for f in flags) + " ENABLED:b OCCUR_ONCE:b CHANCE:i ORDER:i")
    return sg.fields(body, layout)


def add_event(scen: sg.Sg, name: str, affected: set[str], verify: set[str],
              conds: list[bytes], effects: list[bytes]) -> tuple[sg.Sg, str]:
    elf = has_elf_flags(scen)
    orders = [int(event_head(r.body, elf)["ORDER"]) for r in scen.find(WHAT_EVENT)]
    eid = scen.next_ids("EV")[0]
    body = event_body(eid, name, affected, verify, max(orders, default=0) + 1, conds, effects, elf)
    return scen.appended(sg.Record(WHAT_EVENT, eid, body)), eid


def parse_own_event(body: bytes, elf: bool) -> dict:
    """Разбор события, собранного add_event (условия 17 и эффекты 0; других здесь нет)."""
    flags = RACE_FLAGS if elf else RACE_FLAGS[:5]
    c = sg.Cursor(body)
    out = c.fields("ID:s NAME_TXT:s " + " ".join(f"{f}:b" for f in flags) + " "
                   + " ".join(f"VER{f}:b" for f in flags) + " ENABLED:b OCCUR_ONCE:b CHANCE:i ORDER:i")
    conds, effects = [], []
    for _ in range(c.int("COND_QTY")):
        cat = c.int("CATEGORY")
        if cat != COND_STACK_EXISTS:
            raise ValueError(f"условие {cat} этим разбором не читается")
        conds.append((cat, c.str("ID_STACK"), c.int("MISC_INT")))
    for _ in range(c.int("EFFECT_QTY")):
        cat = c.int("CATEGORY")
        if cat != EFFECT_WIN_LOSE:
            raise ValueError(f"эффект {cat} этим разбором не читается")
        effects.append((cat, c.int("NUM"), c.bool("WIN_SCEN"), c.str("ID_PLAYER1")))
    out["conds"], out["effects"], out["tail"] = conds, effects, len(body) - c.pos
    return out


def check_end_event(ch: Counter2, scen: sg.Sg, base: sg.Sg, stack_id: str, win: bool, player: str) -> None:
    """Ровно одно новое событие: гибель отряда stack_id -> победа/поражение player; отряд есть на
    карте и принадлежит нейтралам; ни одно событие основы не выключено."""
    old = {r.obj_id for r in base.find(WHAT_EVENT)}
    new = [r for r in scen.find(WHAT_EVENT) if r.obj_id not in old]
    ch.count("end_event", len(new) == 1, f"новых событий {len(new)}")
    if len(new) != 1:
        return
    elf = has_elf_flags(scen)
    try:
        ev = parse_own_event(new[0].body, elf)
    except ValueError as e:
        ch.count("end_event", False, f"не разбирается: {e}")
        return
    ch.count("end_event", ev["tail"] == 0, f"хвост после эффектов {ev['tail']} байт")
    ch.count("end_event", ev["ENABLED"] == 1 and ev["OCCUR_ONCE"] == 1 and ev["CHANCE"] == 100,
             f"ENABLED {ev['ENABLED']} OCCUR_ONCE {ev['OCCUR_ONCE']} CHANCE {ev['CHANCE']}")
    ch.count("end_event", ev["HUMAN"] == 1 and ev["VERHUMAN"] == 1, "событие не для Империи")
    ch.count("end_event", ev["conds"] == [(COND_STACK_EXISTS, stack_id, 1)], f"условия {ev['conds']}")
    ch.count("end_event", ev["effects"] == [(EFFECT_WIN_LOSE, 0, int(win), player)], f"эффекты {ev['effects']}")
    orders = [int(event_head(r.body, elf)["ORDER"]) for r in base.find(WHAT_EVENT)]
    ch.count("end_event", int(ev["ORDER"]) == max(orders, default=0) + 1, f"ORDER {ev['ORDER']}")
    target = [r for r in scen.find(sg.WHAT_STACK) if r.obj_id == stack_id]
    ch.count("end_event", len(target) == 1, f"отряда {stack_id} нет")
    if target:
        owner = sg.parse_stack(target[0].body)["OWNER"]
        ch.count("end_event", owner == players(scen)[NEUTRAL]["PLAYER_ID"], f"владелец цели {owner}")
    for r in base.find(WHAT_EVENT):
        ch.count("end_event", event_head(record(scen, r.obj_id).body, elf)["ENABLED"]
                 == event_head(r.body, elf)["ENABLED"], f"событие основы {r.obj_id} переключено")


# ------------------------------------------------------------------ руина, заклинания, SOURCE_M

RUIN_HEAD = "RUIN_ID:s TITLE:s DESC:s IMAGE:i POS_X:i POS_Y:i CASH:s ITEM:s LOOTER:s AIPRIORITY:i"


def parse_ruin(body: bytes) -> tuple[dict, list[tuple[str, str]], int, int]:
    """(поля, [(RUIN_ID, VISITER)], начало счётчика, конец списка посетителей)."""
    c = sg.Cursor(body)
    f = c.fields(RUIN_HEAD)
    at = c.pos
    n = c.count()
    vis = [(c.str("RUIN_ID"), c.str("VISITER")) for _ in range(n)]
    return f, vis, at, c.pos


def add_ruin_visitor(scen: sg.Sg, ruin_id: str, player: str) -> sg.Sg:
    """Игрок считается разведавшим руину (так её помечает вор действием «Разведать руину»)."""
    rec = record(scen, ruin_id)
    _f, vis, at, end = parse_ruin(rec.body)
    body = (rec.body[:at] + sg.f_count(ruin_id, len(vis) + 1) + rec.body[at + 14:end]
            + sg.f_str("RUIN_ID", ruin_id) + sg.f_str("VISITER", player) + rec.body[end:])
    return scen.with_record(rec, replace(rec, body=body))


def parse_known(body: bytes) -> list[str]:
    c = sg.Cursor(body)
    out = [c.str("SPELL_ID") for _ in range(c.count())]
    if c.pos != len(body):
        raise ValueError(f"хвост {len(body) - c.pos} байт после списка заклинаний")
    return out


def set_known_spells(scen: sg.Sg, known_id: str, spells: list[str]) -> sg.Sg:
    rec = record(scen, known_id)
    body = sg.f_count(known_id, len(spells)) + b"".join(sg.f_str("SPELL_ID", s) for s in spells)
    return scen.with_record(rec, replace(rec, body=body))


def set_source_m(scen: sg.Sg, value: int) -> sg.Sg:
    rec = scen.one(sg.WHAT_INFO)
    c = sg.Cursor(rec.body)
    c.fields("INFO_ID:s CAMPAIGN:s")
    at = c.pos + len(b"SOURCE_M")
    if rec.body[c.pos:at] != b"SOURCE_M":
        raise ValueError("SOURCE_M не на месте")
    body = rec.body[:at] + bytes([value]) + rec.body[at + 1:]
    return scen.with_record(rec, replace(rec, body=body))


def info_head(scen: sg.Sg) -> dict:
    return sg.fields(scen.one(sg.WHAT_INFO).body, "INFO_ID:s CAMPAIGN:s SOURCE_M:b")


# ------------------------------------------------------------------ общие сторожа карты

def check_reload(ch: Counter2, data: bytes, base: sg.Sg, added: int) -> sg.Sg | None:
    try:
        scen = sg.Sg.loads(data)
    except ValueError as e:
        ch.count("sg_reload", False, f"Sg.loads: {e}")
        return None
    ch.count("sg_reload", len(scen.records) == len(base.records) + added,
             f"объектов {len(scen.records)}, ждали {len(base.records)} + {added}")
    ch.count("sg_reload", scen.dumps() == data, "повторная запись разобранного файла не равна файлу")
    return scen


def check_others(ch: Counter2, scen: sg.Sg, base: sg.Sg, allowed: set[str]) -> list[str]:
    """Записи основы на своих местах; изменены только разрешённые (ScenarioInfo и план — всегда)."""
    allowed = set(allowed) | {base.one(sg.WHAT_INFO).obj_id, base.one(sg.WHAT_PLAN).obj_id}
    out = []
    for i, r in enumerate(base.records):
        new = scen.records[i] if i < len(scen.records) else None
        if new is None or new.obj_id != r.obj_id or new.what != r.what:
            ch.count("other_records_bytes", False, f"запись {i} {r.obj_id} сдвинута или пропала")
            continue
        if new.body != r.body:
            out.append(r.obj_id)
        ch.count("other_records_bytes", new.body == r.body or r.obj_id in allowed,
                 f"{r.obj_id} {r.what} отличается от основы")
    return out


def check_name(ch: Counter2, scen: sg.Sg, base: sg.Sg, name: str) -> None:
    """Имя в списке игры — quest_name заголовка; NAME в ScenarioInfo — то же."""
    ch.count("name", sg.scenario_name(scen) == name, f"ScenarioInfo.NAME = {sg.scenario_name(scen)!r}")
    ch.count("name", sg.header_name(scen) == name, f"quest_name заголовка = {sg.header_name(scen)!r}")
    at = sg.header_name_offset(base.header)
    ch.count("name", scen.header[:at] + scen.header[at + 256:] == base.header[:at] + base.header[at + 256:],
             "заголовок вне quest_name изменён")


def check_plan_added(ch: Counter2, scen: sg.Sg, base: sg.Sg, added: list[tuple[int, int, str]]) -> None:
    size0, old = sg.parse_plan(base.one(sg.WHAT_PLAN).body)
    size1, new = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    ch.count("plan", size0 == size1 and new[:len(old)] == old, "план основы изменён")
    ch.count("plan", sorted(new[len(old):]) == sorted(added), f"дописано {new[len(old):]}, ждали {added}")
    taken = {(x, y) for x, y, _ in old}
    for x, y, e in added:
        ch.count("plan", (x, y) not in taken, f"клетка {x},{y} ({e}) уже занята")


def flip_foreign(scen: sg.Sg) -> sg.Sg:
    rec = scen.find("MidgardMapBlock")[0]
    return scen.with_record(rec, replace(rec, body=rec.body[:-1] + bytes([rec.body[-1] ^ 1])))


# ------------------------------------------------------------------ карта «убей отряд -> конец сценария»
#
# Герой Империи и нейтральная цель стоят вплотную; бой с целью кончается её гибелью, событие
# (условие 17 «отряда нет») даёт победу Империи или поражение. Отряды дописывает p3a_common
# (Mercantile по умолчанию; Fardale's Web — если до импорта загружен p4a_common).

WIN_FLAGS = ({"HUMAN"}, {"HUMAN"})                         # как «Z - EMPIRE - WIN» Arrival of the Pilgrims
LOSE_FLAGS = ({"HUMAN", "NEUTRAL"}, {"HUMAN", "NEUTRAL"})  # как «KARASTEN KILLED» там же


def p3a():
    import p3a_common as pa  # noqa: WPS433 — лениво: тянет _batch1a/common
    return pa


def end_map_build(base: sg.Sg, t, parties, name: str, win: bool, source_m: int | None = None) -> sg.Sg:
    pa = p3a()
    scen, got = pa.add_parties(base, t, parties)
    target = got["target"].obj_id
    empire = str(players(base)[EMPIRE]["PLAYER_ID"])
    affected, verify = WIN_FLAGS if win else LOSE_FLAGS
    effect = effect_end(True, empire) if win else effect_end(False, NOBODY)
    title = "UI: target killed - Empire wins" if win else "UI: hostage killed - scenario lost"
    scen, _eid = add_event(scen, title, affected, verify, [cond_stack_gone(target)], [effect])
    if source_m is not None:
        scen = set_source_m(scen, source_m)
    return sg.set_scenario_name(scen, name)


def end_map_checks(ch: Counter2, scen: sg.Sg, base: sg.Sg, t, parties, win: bool) -> dict:
    pa = p3a()
    import f7_twocap as tw
    got = pa.check_parties(ch, scen, base, t, parties)
    pa.check_cells(ch, base, parties, {frozenset(("hero", "target"))})
    pa.check_plan(ch, scen, base, parties)
    roles = dict(zip([p.role for p in parties], pa.added_stacks(scen, base)))
    by_role = {p.role: p for p in parties}
    if "target" not in roles or "hero" not in roles:
        ch.count("design", False, "нет героя или цели")
        return got
    target = roles["target"].obj_id
    empire = str(players(base)[EMPIRE]["PLAYER_ID"])
    check_end_event(ch, scen, base, target, win, empire if win else NOBODY)
    hero, tgt = by_role["hero"], by_role["target"]
    pos = {k: (int(s["POS_X"]), int(s["POS_Y"])) for k, s in
           ((k, sg.parse_stack(r.body)) for k, r in roles.items())}
    ch.count("design", pa.cm.cheb(pos["hero"], pos["target"]) == 1,
             f"герой {pos['hero']} не вплотную к цели {pos['target']} (в файле)")
    ch.count("design", tgt.owner == "neutral" and not tgt.units, "цель не одиночный нейтрал")
    check_map_leaders(ch, t, parties)
    dmg, hp = tw.hit(t, hero.leader, False), int(t.unit(tgt.leader)["HIT_POINT"])
    ch.count("design", dmg >= hp, f"удар героя {dmg} < HP цели {hp}: бой не в один удар")
    st = sg.parse_stack(roles["hero"].body)
    # экспорт героя (0x4C9DA0) пропускает только отряд с CREAT_LVL == 1
    ch.count("design", st["CREAT_LVL"] == 1, f"CREAT_LVL героя {st['CREAT_LVL']}")
    return got


def check_map_leaders(ch: Counter2, t, parties) -> None:
    """Вожак отряда — юнит-лидер (UNIT_CAT != 0). У рядового (Peasant g000uu5001) нет картинки на
    карте (IsoUnit): отряд невидим, а бой с ним и ход нейтралов роняют игру (срез 19.09, заходы 1–4)."""
    for p in parties:
        cat = int(t.unit(p.leader)["UNIT_CAT"])
        ch.count("map_leader", cat != 0, f"{p.role}: вожак {p.leader} — рядовой (UNIT_CAT 0)")


def end_event_sabotage(scen: sg.Sg, base: sg.Sg) -> bytes:
    """Порча: условие события «отряд есть» вместо «отряда нет»."""
    old = {r.obj_id for r in base.find(WHAT_EVENT)}
    rec = next(r for r in scen.find(WHAT_EVENT) if r.obj_id not in old)
    body = rec.body.replace(b"MISC_INT" + struct.pack("<i", 1), b"MISC_INT" + struct.pack("<i", 0))
    return scen.with_record(rec, replace(rec, body=body)).dumps()


# ------------------------------------------------------------------ запуск одной карты .sg

def game_snapshot() -> dict[str, str]:
    files = sorted(p for d in (GAME_EXPORTS, GAME_CAMPAIGN, PRISTINE) if d.exists()
                   for p in d.iterdir() if p.is_file())
    return {p.relative_to(MOD).as_posix(): sha(p.read_bytes()) for p in files}


def run_checks(spec, data: bytes, base: sg.Sg, t, quiet: bool = False) -> tuple[Counter2, list[str]]:
    ch = Counter2(quiet)
    scen = check_reload(ch, data, base, spec.ADDED)
    changed: list[str] = []
    if scen is not None:
        changed = check_others(ch, scen, base, set(spec.CHANGED))
        check_name(ch, scen, base, spec.NAME)
        spec.checks(ch, scen, base, t)
    return ch, changed


def selftest(spec, base: sg.Sg, t) -> int:
    honest = spec.build(base, t)
    ch, _ = run_checks(spec, honest.dumps(), base, t, quiet=True)
    bad = ch.failed() | ch.empty()
    print(f"selftest честная карта: провалов {len(bad)} {sorted(bad)}")
    missed = bool(bad)
    cases = [
        ("байт в чужой записи (MidgardMapBlock)", "other_records_bytes", flip_foreign(honest).dumps()),
        ("чужое имя", "name", sg.set_scenario_name(honest, spec.NAME + "-x").dumps()),
        ("обрезанный файл", "sg_reload", honest.dumps()[:-20]),
    ] + spec.sabotages(base, t)
    for what, guard, data in cases:
        ch, _ = run_checks(spec, data, base, t, quiet=True)
        caught = guard in ch.failed()
        missed += not caught
        print(f"selftest порча «{what}»: сторож {guard} {'ПОЙМАЛ' if caught else 'ПРОПУСТИЛ'}"
              f" (упали: {sorted(ch.failed())})")
    print(f"selftest пропущенных порч {missed}")
    return 1 if missed else 0


def report(spec, data: bytes, base: sg.Sg, t) -> int:
    ch, changed = run_checks(spec, data, base, t)
    ch.print()
    print(f"изменённые записи основы: {changed}; дописано записей: {spec.ADDED}")
    return 1 if ch.failed() or ch.empty() else 0


def install_copy(src: Path, dest_dir: Path, name: str) -> int:
    """Копия готового файла в папку игры: только новым файлом; тот же файл — не ошибка."""
    dest = dest_dir / name
    data = src.read_bytes()
    if dest.exists():
        same = dest.read_bytes() == data
        print(f"{dest} уже есть: {'тот же файл' if same else 'ДРУГОЙ файл — не трогаю'}")
        return 0 if same else 1
    with dest.open("xb") as fh:
        fh.write(data)
    print(f"установлено {dest} sha256 {sha(dest.read_bytes())}")
    return 0 if dest.read_bytes() == data else 1


def run(spec, argv: list[str] | None = None) -> int:
    """--plan | --verify | --selftest | (запись в out/) | --install.
    spec: NAME, BASE, ADDED, CHANGED, build(base, t), checks(ch, scen, base, t),
    sabotages(base, t), print_plan(base, t); tables() — таблицы игры."""
    map_dir = Path(spec.__file__).resolve().parent
    ap = argparse.ArgumentParser(description=spec.__doc__.split("\n")[0])
    ap.add_argument("--plan", action="store_true", help="собрать в памяти и проверить, без записи")
    ap.add_argument("--verify", action="store_true", help="проверить записанную карту (out/ и копию в игре)")
    ap.add_argument("--selftest", action="store_true", help="порчи ловятся сторожами")
    ap.add_argument("--install", action="store_true", help="скопировать out/ в game-dev/Exports новым файлом")
    ap.add_argument("--out", type=Path, default=map_dir / "out" / f"{spec.NAME}.sg")
    a = ap.parse_args(argv)
    before = game_snapshot()
    base = sg.Sg.read(spec.BASE)
    t = spec.tables()
    print(f"основа {spec.BASE.name} sha256 {sha(spec.BASE.read_bytes())[:16]} записей {len(base.records)}")
    installed = GAME_EXPORTS / f"{spec.NAME}.sg"
    if a.selftest:
        rc = selftest(spec, base, t)
    elif a.plan:
        spec.print_plan(base, t)
        rc = report(spec, spec.build(base, t).dumps(), base, t)
    elif a.verify:
        rc = 0
        for path in (a.out, installed):
            if not path.exists():
                print(f"{path}: нет файла")
                rc |= path == a.out
                continue
            print(f"проверяю {path} sha256 {sha(path.read_bytes())}")
            rc |= report(spec, path.read_bytes(), base, t)
        if a.out.exists() and installed.exists():
            same = a.out.read_bytes() == installed.read_bytes()
            print(f"копия в игре {'совпадает' if same else 'ОТЛИЧАЕТСЯ'} с out/")
            rc |= not same
    elif a.install:
        if not a.out.exists():
            print(f"{a.out}: сначала соберите карту (без ключей)")
            return 1
        rc = report(spec, a.out.read_bytes(), base, t) or install_copy(a.out, GAME_EXPORTS, f"{spec.NAME}.sg")
        return rc
    else:
        spec.print_plan(base, t)
        if a.out.exists():
            print(f"{a.out} уже есть — не перезаписываю")
            return 1
        a.out.parent.mkdir(parents=True, exist_ok=True)
        with a.out.open("xb") as fh:
            fh.write(spec.build(base, t).dumps())
        print(f"записано {a.out} sha256 {sha(a.out.read_bytes())}")
        rc = report(spec, a.out.read_bytes(), base, t)
    after = game_snapshot()
    ch = Counter2()
    for name, digest in before.items():
        ch.count("game_untouched", after.get(name) == digest, f"{name} изменён или пропал")
    ch.count("game_untouched", set(after) == set(before), f"новые файлы {sorted(set(after) - set(before))}")
    ch.print()
    return rc or bool(ch.failed())


# ------------------------------------------------------------------ своя кампания (.csg)
#
# Контейнер MQDB (как .ff): заголовок 28 байт «MQDB», 0, 9, 0, 0, 0, 0x5C; записи «MQRC», 0, id,
# размер, выделено, занято(1), 0 и данные. Запись 1 — «MFF\0» + 5, запись 0 — оглавление 1024 байта
# (число занятых; по 16 байт id, размер, выделено, смещение заголовка записи; число свободных; ...),
# запись 2 — список файлов (число; имя 256 байт + id). Файлы кампании: HEADER.CPN (340 байт),
# ORDER.CPN (число и имена сценариев по порядку) и сами .sg. Сверено по четырём .csg game-dev/Campaign;
# HEADER.CPN читается игрой целиком 0x154 байт (0x66AEEA), новый заголовок пишет 0x66B0CA:
# +0 версия (1, у новых редакторов 100), +4 имя, +0x25 описание, +0x125 автор, +0x148 id расы
# игрока, +0x14C int (уровень лидера?), +0x150 байт (см. cpn_header).

MQDB_HEAD = b"MQDB" + struct.pack("<6i", 0, 9, 0, 0, 0, 0x5C)
REC_TOC, REC_MFF, REC_FILES = 0, 1, 2
TOC_SIZE = 1024
HEADER_SIZE = 0x154


def mqrc(rid: int, data: bytes) -> bytes:
    return b"MQRC" + struct.pack("<6i", 0, rid, len(data), len(data), 1, 0) + data


ID_TYPE_RACE = 3                                   # IdType::Race ('RR'), midgardid.h


def race_id_value(race_index: int) -> int:
    """CMidgardID G000RRxxxx в числовом виде: тип в битах 16…21, номер — в младших."""
    return (ID_TYPE_RACE << 16) | race_index


def cpn_header(name: str, desc: str, author: str, race_index: int = 0, leader_level: int = 1) -> bytes:
    """+0x148 — CMidgardID расы игрока (0x57F227 разбирает его как id; в образцах G000RR0001 у
    AVALANCHE, где во всех сценариях есть Орды нежити, G000RR0003 у Bloody Doom — Кланы,
    G000RR0002 у «Пути Проклятых» — Легионы, G000RR0000 у «Эллиона» — Империя), +0x14C — int
    (1, 1, 1, 5; окно пишет «Уровень лидера»), +0x150 — байт 0."""
    def fixed(text: str, size: int) -> bytes:
        raw = text.encode("cp1251")
        if len(raw) >= size:
            raise ValueError(f"«{text}» длиннее {size - 1} байт")
        return raw.ljust(size, b"\0")
    body = (struct.pack("<i", 1) + fixed(name, 33) + fixed(desc, 256) + fixed(author, 35)
            + struct.pack("<Ii", race_id_value(race_index), leader_level) + b"\0" * 4)
    assert len(body) == HEADER_SIZE
    return body


def cpn_order(names: list[str]) -> bytes:
    return struct.pack("<i", len(names)) + b"".join(n.encode("cp1251") + b"\0" for n in names)


def build_csg(header: bytes, order: bytes, files: list[tuple[str, bytes]]) -> bytes:
    """files — [(имя как в ORDER.CPN, байты .sg)]. Id файлов 5, 7, 9… как у образцов."""
    entries = [("HEADER.CPN", header), ("ORDER.CPN", order)] + [(n.upper(), d) for n, d in files]
    ids = {name: 5 + 2 * k for k, (name, _d) in enumerate(entries)}
    listing = struct.pack("<i", len(entries)) + b"".join(
        name.encode("cp1251").ljust(256, b"\0") + struct.pack("<i", ids[name])
        for name, _d in sorted(entries))
    body = {ids[n]: d for n, d in entries}
    body[REC_MFF] = b"MFF\0" + struct.pack("<i", 5)
    body[REC_FILES] = listing
    layout = [REC_MFF, REC_TOC] + sorted(ids.values()) + [REC_FILES]
    offsets, pos = {}, len(MQDB_HEAD)
    for rid in layout:
        offsets[rid] = pos
        pos += 28 + (TOC_SIZE if rid == REC_TOC else len(body[rid]))
    toc_ids = sorted(offsets)
    toc = struct.pack("<i", len(toc_ids)) + b"".join(
        struct.pack("<4i", rid, TOC_SIZE if rid == REC_TOC else len(body[rid]),
                    TOC_SIZE if rid == REC_TOC else len(body[rid]), offsets[rid]) for rid in toc_ids)
    toc += struct.pack("<i", 0)
    body[REC_TOC] = toc.ljust(TOC_SIZE, b"\0")
    out = MQDB_HEAD + b"".join(mqrc(rid, body[rid]) for rid in layout)
    if offsets[REC_TOC] + 28 != 0x5C:
        raise ValueError("оглавление не на 0x5C")
    return out


def parse_csg(data: bytes) -> dict:
    """Разбор .csg с проверкой оглавления: {"records", "toc", "files", "header", "order"}."""
    if data[:4] != b"MQDB":
        raise ValueError("не MQDB")
    toc_at = struct.unpack_from("<i", data, 24)[0]
    recs, pos = {}, 28
    while pos + 28 <= len(data) and data[pos:pos + 4] == b"MQRC":
        _z, rid, size, alloc, used, _z2 = struct.unpack_from("<6i", data, pos + 4)
        if used:
            recs[rid] = (pos, size, alloc, data[pos + 28:pos + 28 + size])
        pos += 28 + alloc
    if pos != len(data):
        raise ValueError(f"после записей {len(data) - pos} байт мусора")
    if REC_TOC not in recs or recs[REC_TOC][0] + 28 != toc_at:
        raise ValueError("оглавление не там, куда указывает заголовок")
    toc_raw = recs[REC_TOC][3]
    n = struct.unpack_from("<i", toc_raw, 0)[0]
    toc = {}
    for k in range(n):
        rid, size, alloc, off = struct.unpack_from("<4i", toc_raw, 4 + 16 * k)
        toc[rid] = (size, alloc, off)
    for rid, (off, size, alloc, _d) in recs.items():
        if toc.get(rid) != (size, alloc, off):
            raise ValueError(f"запись {rid}: оглавление {toc.get(rid)}, в файле {(size, alloc, off)}")
    listing = recs[REC_FILES][3]
    files = {}
    for k in range(struct.unpack_from("<i", listing, 0)[0]):
        raw = listing[4 + 260 * k:4 + 260 * k + 256].split(b"\0")[0].decode("cp1251")
        rid = struct.unpack_from("<i", listing, 4 + 260 * k + 256)[0]
        if rid not in recs:
            raise ValueError(f"файл {raw}: записи {rid} нет")
        files[raw] = recs[rid][3]
    head = files["HEADER.CPN"]
    header = {"version": struct.unpack_from("<i", head, 0)[0],
              "name": head[4:37].split(b"\0")[0].decode("cp1251"),
              "desc": head[37:293].split(b"\0")[0].decode("cp1251"),
              "author": head[293:328].split(b"\0")[0].decode("cp1251"),
              "race": struct.unpack_from("<I", head, 328)[0], "level": struct.unpack_from("<i", head, 332)[0],
              "flag": head[336], "size": len(head)}
    od = files["ORDER.CPN"]
    order, p = [], 4
    for _ in range(struct.unpack_from("<i", od, 0)[0]):
        end = od.index(b"\0", p)
        order.append(od[p:end].decode("cp1251"))
        p = end + 1
    return {"records": recs, "toc": toc, "files": files, "header": header, "order": order,
            "mff": recs[REC_MFF][3]}


def csg_rebuild_selftest() -> int:
    """Писатель .csg пересобирает четыре образца из их же частей; разбор сходится с оригиналом."""
    bad = 0
    for path in sorted(GAME_CAMPAIGN.glob("*.csg")):
        orig = parse_csg(path.read_bytes())
        files = orig["files"]
        by_upper = {k.upper(): v for k, v in files.items()}
        sgs = [(n, by_upper[n.upper()]) for n in orig["order"]]
        rebuilt = parse_csg(build_csg(files["HEADER.CPN"], files["ORDER.CPN"], sgs))
        same = (rebuilt["files"] == files and rebuilt["header"] == orig["header"]
                and rebuilt["order"] == orig["order"] and rebuilt["mff"] == orig["mff"])
        bad += not same
        print(f"csg пересборка {path.name}: файлов {len(files)}, {'сошлось' if same else 'РАЗОШЛОСЬ'}")
    return 1 if bad else 0
