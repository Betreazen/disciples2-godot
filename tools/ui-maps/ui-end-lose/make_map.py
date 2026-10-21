#!/usr/bin/env python3
"""ui-end-lose: сценарий, проигрываемый одним боем, — окно конца сценария DLG_END_WIN_LOSE_ в
варианте поражения.

Запуск из корня disciples-godot:
    python tools/ui-maps/ui-end-lose/make_map.py --plan
    python tools/ui-maps/ui-end-lose/make_map.py            # запись в out/
    python tools/ui-maps/ui-end-lose/make_map.py --verify
    python tools/ui-maps/ui-end-lose/make_map.py --selftest
    python tools/ui-maps/ui-end-lose/make_map.py --install  # копия в game-dev/Exports

Основа — Mercantile. Геометрия как у ui-end-win: Рыцарь на пегасе Империи (18, 19), одиночный
нейтральный Крестьянин-«заложник» (19, 19). Событие «заложника больше нет -> поражение» записано
как штатное «KARASTEN KILLED» из Arrival of the Pilgrims: затронуты люди и нейтралы, эффект 0 с
WIN_SCEN 0 и игроком G000000000. Экспорта героя при поражении нет: CCmdExportLeaderMsg сервер шлёт
только победителю (0x421077); после DLG_END_WIN_LOSE_ проигравший уходит в главное меню (0x40505B).
"""
from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

_spec = importlib.util.spec_from_file_location("ui_common", Path(__file__).resolve().parents[1] / "common.py")
uc = importlib.util.module_from_spec(_spec)
sys.modules["ui_common"] = uc
_spec.loader.exec_module(uc)  # type: ignore[union-attr]
pa = uc.p3a()
sg = uc.sg

NAME = "ui-end-lose"
BASE = pa.cm.BASE                                   # game-pristine/Exports/Mercantile.sg
KNIGHT, PEASANT = "g000uu0019", "g000uu5101"
PARTIES = (
    pa.Party("hero", KNIGHT, (18, 19)),
    pa.Party("target", PEASANT, (19, 19), owner="neutral"),
)
ADDED = 2 * 2 + 1
CHANGED: set[str] = set()


def tables():
    return pa.Tables()


def build(base: sg.Sg, t, parties=PARTIES) -> sg.Sg:
    return uc.end_map_build(base, t, parties, NAME, win=False)


def checks(ch, scen: sg.Sg, base: sg.Sg, t) -> None:
    uc.end_map_checks(ch, scen, base, t, PARTIES, win=False)


def sabotages(base: sg.Sg, t):
    from dataclasses import replace
    moved = (PARTIES[0], replace(PARTIES[1], xy=(21, 19)))
    return [
        ("заложник отодвинут от героя", "design", build(base, t, moved).dumps()),
        ("условие «отряд есть» вместо «нет»", "end_event", uc.end_event_sabotage(build(base, t), base)),
        ("событие победы вместо поражения", "end_event",
         uc.end_map_build(base, t, PARTIES, NAME, win=True).dumps()),
    ]


def print_plan(base: sg.Sg, t) -> None:
    for p in PARTIES:
        print(f"  {p.role}: {t.name(p.leader)} ({p.leader}) в {p.xy}, владелец {p.owner}")
    print("  гибель заложника -> WIN_SCEN 0, игрок G000000000")


if __name__ == "__main__":
    sys.exit(uc.run(sys.modules[__name__]))
