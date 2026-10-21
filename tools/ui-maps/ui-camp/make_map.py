#!/usr/bin/env python3
"""ui-camp: своя кампания из двух коротких сценариев — конец главы (DLG_FAKE_CARRY_OVER,
DLG_CARRY_ITEM_LEADER) и конец кампании с экспортом героя.

Запуск из корня disciples-godot:
    python tools/ui-maps/ui-camp/make_map.py --plan
    python tools/ui-maps/ui-camp/make_map.py            # запись out/ui-camp-1.sg, ui-camp-2.sg, ui-camp.csg
    python tools/ui-maps/ui-camp/make_map.py --verify
    python tools/ui-maps/ui-camp/make_map.py --selftest
    python tools/ui-maps/ui-camp/make_map.py --install  # копия ui-camp.csg в game-dev/Campaign

Оба сценария — Mercantile с геометрией ui-end-win (Рыцарь на пегасе (18, 19), Крестьянин (19, 19),
гибель Крестьянина -> победа Империи), SOURCE_M 0 и CAMPAIGN C000CC0001, как у сценариев четырёх
.csg в game-dev/Campaign. У героя первого сценария в сумке два предмета — чтобы окну переноса было
что показать. Файл кампании — контейнер MQDB (см. common.py, «своя кампания»).

Цепочка по коду: победа в сценарии, который НЕ последний, -> CCmdScenarioEndMsg с флагом
«последний» = 0 (0x42128F) -> DLG_END_WIN_LOSE_ -> CFakeCarryOver (DLG_FAKE_CARRY_OVER, пустая
подложка, 0x481189) -> CCarryOverInterf (DLG_CARRY_ITEM_LEADER, 0x4812AE) -> следующий сценарий.
Победа в последнем -> вопрос об экспорте героя (0x489A63: номер сценария своей кампании =
число сценариев − 1) -> DLG_LEADER_PICKER -> DLG_END_WIN_LOSE_ -> главное меню.
"""
from __future__ import annotations

import argparse
import importlib.util
import sys
from pathlib import Path
from types import SimpleNamespace

_spec = importlib.util.spec_from_file_location("ui_common", Path(__file__).resolve().parents[1] / "common.py")
uc = importlib.util.module_from_spec(_spec)
sys.modules["ui_common"] = uc
_spec.loader.exec_module(uc)  # type: ignore[union-attr]
pa = uc.p3a()
sg = uc.sg

NAME = "ui-camp"
BASE = pa.cm.BASE
KNIGHT, PEASANT = "g000uu0019", "g000uu5101"
BRONZE_RING, CELERITY_SCROLL = "G000IG7001", "G000IG5002"
AUTHOR = "disciples-godot ui-maps"
RACE_EMPIRE_INDEX, LEADER_LEVEL = 0, 1             # раса игрока G000RR0000; уровень — как у 3 из 4 образцов
SCENARIOS = (
    ("ui-camp-1", (pa.Party("hero", KNIGHT, (18, 19), items=(BRONZE_RING, CELERITY_SCROLL)),
                   pa.Party("target", PEASANT, (19, 19), owner="neutral"))),
    ("ui-camp-2", (pa.Party("hero", KNIGHT, (18, 19)),
                   pa.Party("target", PEASANT, (19, 19), owner="neutral"))),
)
OUT = Path(__file__).resolve().parent / "out"


def scen_spec(name: str, parties) -> SimpleNamespace:
    def checks(ch, scen, base, t):
        uc.end_map_checks(ch, scen, base, t, parties, win=True)
        info = uc.info_head(scen)
        ch.count("campaign_info", info["SOURCE_M"] == 0 and info["CAMPAIGN"] == "C000CC0001",
                 f"ScenarioInfo {info}")
    return SimpleNamespace(NAME=name, ADDED=2 * 2 + 1 + sum(len(p.items) for p in parties),
                           CHANGED=set(), checks=checks,
                           build=lambda base, t: uc.end_map_build(base, t, parties, name, True, source_m=0))


SPECS = [scen_spec(n, p) for n, p in SCENARIOS]


def order_names() -> list[str]:
    return [f"{s.NAME}.sg" for s in SPECS]


def build_all(base: sg.Sg, t) -> tuple[list[bytes], bytes]:
    sgs = [s.build(base, t).dumps() for s in SPECS]
    header = uc.cpn_header(NAME, "", AUTHOR, RACE_EMPIRE_INDEX, LEADER_LEVEL)
    csg = uc.build_csg(header, uc.cpn_order(order_names()), list(zip(order_names(), sgs)))
    return sgs, csg


def check_all(sgs: list[bytes], csg: bytes, base: sg.Sg, t, quiet: bool = False) -> uc.Counter2:
    total = uc.Counter2(quiet)
    for spec, data in zip(SPECS, sgs):
        ch, _changed = uc.run_checks(spec, data, base, t, quiet=True)
        for k, (ok, bad) in ch.rows.items():
            total.rows.setdefault(f"{spec.NAME}:{k}", [0, 0])
            total.rows[f"{spec.NAME}:{k}"][0] += ok
            total.rows[f"{spec.NAME}:{k}"][1] += bad
        total.notes += [f"{spec.NAME}: {n.strip()}" for n in ch.notes]
    try:
        parsed = uc.parse_csg(csg)
    except (ValueError, KeyError, IndexError) as e:
        total.count("csg", False, f"разбор: {e}")
        return total
    h = parsed["header"]
    total.count("csg", h["name"] == NAME and h["author"] == AUTHOR and h["version"] == 1
                and h["size"] == uc.HEADER_SIZE and h["race"] == uc.race_id_value(RACE_EMPIRE_INDEX)
                and h["level"] == LEADER_LEVEL and h["flag"] == 0, f"HEADER.CPN {h}")
    total.count("csg", parsed["order"] == order_names(), f"ORDER.CPN {parsed['order']}")
    files = {k.upper(): v for k, v in parsed["files"].items()}
    for name, data in zip(order_names(), sgs):
        total.count("csg", files.get(name.upper()) == data, f"{name} в .csg не совпадает со сценарием")
    total.count("csg", set(files) == {"HEADER.CPN", "ORDER.CPN"} | {n.upper() for n in order_names()},
                f"файлы {sorted(files)}")
    total.count("csg", parsed["mff"] == b"MFF\0\x05\0\0\0", f"запись 1 {parsed['mff']!r}")
    return total


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--plan", action="store_true")
    ap.add_argument("--verify", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--install", action="store_true")
    a = ap.parse_args(argv)
    before = uc.game_snapshot()
    base, t = sg.Sg.read(BASE), pa.Tables()
    paths = [OUT / n for n in order_names()] + [OUT / f"{NAME}.csg"]
    installed = uc.GAME_CAMPAIGN / f"{NAME}.csg"
    rc = 0
    if a.selftest:
        rc |= uc.csg_rebuild_selftest()
        sgs, csg = build_all(base, t)
        ch = check_all(sgs, csg, base, t, quiet=True)
        print(f"selftest честная кампания: провалов {sorted(ch.failed() | ch.empty())}")
        rc |= bool(ch.failed() | ch.empty())
        bad_csg = csg.replace(b"UI-CAMP-2.SG", b"UI-CAMP-X.SG")
        for what, data, guard in (("имя сценария в списке файлов", bad_csg, "csg"),
                                  ("обрезанный .csg", csg[:-5], "csg"),
                                  ("сценарий без события", (sgs[0], uc.flip_foreign(sg.Sg.loads(sgs[1])).dumps()),
                                   "ui-camp-2:other_records_bytes")):
            if isinstance(data, tuple):
                ch = check_all(list(data), csg, base, t, quiet=True)
            else:
                ch = check_all(sgs, data, base, t, quiet=True)
            caught = guard in ch.failed()
            rc |= not caught
            print(f"selftest порча «{what}»: {guard} {'ПОЙМАЛ' if caught else 'ПРОПУСТИЛ'}")
    elif a.plan:
        sgs, csg = build_all(base, t)
        ch = check_all(sgs, csg, base, t)
        ch.print()
        print(f".csg {len(csg)} байт, сценарии {[len(s) for s in sgs]}")
        rc = bool(ch.failed() | ch.empty())
    elif a.verify:
        if not all(p.exists() for p in paths):
            print(f"нет файлов: {[str(p) for p in paths if not p.exists()]}")
            return 1
        ch = check_all([p.read_bytes() for p in paths[:-1]], paths[-1].read_bytes(), base, t)
        ch.print()
        rc = bool(ch.failed() | ch.empty())
        if installed.exists():
            same = installed.read_bytes() == paths[-1].read_bytes()
            print(f"{installed}: {'совпадает' if same else 'ОТЛИЧАЕТСЯ'} с out/")
            rc |= not same
        else:
            print(f"{installed}: ещё не установлен")
    elif a.install:
        if not paths[-1].exists():
            print("сначала соберите кампанию (без ключей)")
            return 1
        ch = check_all([p.read_bytes() for p in paths[:-1]], paths[-1].read_bytes(), base, t)
        if ch.failed() | ch.empty():
            ch.print()
            return 1
        return uc.install_copy(paths[-1], uc.GAME_CAMPAIGN, f"{NAME}.csg")
    else:
        if any(p.exists() for p in paths):
            print(f"уже есть: {[p.name for p in paths if p.exists()]} — не перезаписываю")
            return 1
        sgs, csg = build_all(base, t)
        OUT.mkdir(parents=True, exist_ok=True)
        for p, data in zip(paths, sgs + [csg]):
            with p.open("xb") as fh:
                fh.write(data)
            print(f"записано {p} sha256 {uc.sha(p.read_bytes())}")
        ch = check_all([p.read_bytes() for p in paths[:-1]], paths[-1].read_bytes(), base, t)
        ch.print()
        rc = bool(ch.failed() | ch.empty())
    after = uc.game_snapshot()
    g = uc.Counter2()
    for name, digest in before.items():
        g.count("game_untouched", after.get(name) == digest, f"{name} изменён или пропал")
    g.count("game_untouched", set(after) == set(before), f"новые файлы {sorted(set(after) - set(before))}")
    g.print()
    return rc or bool(g.failed())


if __name__ == "__main__":
    sys.exit(main())
