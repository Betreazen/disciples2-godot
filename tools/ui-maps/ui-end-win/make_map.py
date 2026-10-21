#!/usr/bin/env python3
"""ui-end-win: сценарий, выигрываемый одним боем, — окно конца сценария DLG_END_WIN_LOSE_ и перед ним
вопрос об экспорте героя с выбором героя DLG_LEADER_PICKER.

Запуск из корня disciples-godot:
    python tools/ui-maps/ui-end-win/make_map.py --plan
    python tools/ui-maps/ui-end-win/make_map.py            # запись в out/
    python tools/ui-maps/ui-end-win/make_map.py --verify
    python tools/ui-maps/ui-end-win/make_map.py --selftest
    python tools/ui-maps/ui-end-win/make_map.py --install  # копия в game-dev/Exports

Основа — Mercantile (Империя + Эльфы). Добавлены Рыцарь на пегасе Империи (18, 19) и одиночный
нейтральный Крестьянин (19, 19) вплотную к нему, плюс событие «Крестьянина больше нет -> победа
Империи». Цепочка по коду (Discipl2.exe GOG 3.01): победа в последнем сценарии кампании (у квеста
кампания C000CC0001 из одного сценария) -> сервер шлёт CCmdExportLeaderMsg (0x421077) -> клиент
спрашивает «Экспортировать героя?» (CTaskAskExportLeader 0x4D6F79, текст X005TA0894) -> «Да» ->
DLG_LEADER_PICKER (0x4D74B2) -> файл Exports\\<имя героя>.ldr (0x4081BC) -> CCmdScenarioEndMsg ->
DLG_END_WIN_LOSE_ (0x4BC283) -> для квеста главное меню (0x40505B).
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

NAME = "ui-end-win"
BASE = pa.cm.BASE                                   # game-pristine/Exports/Mercantile.sg
KNIGHT, PEASANT = "g000uu0019", "g000uu5101"        # Pegasus Knight 150 HP / удар 50; Peasant (вожак) 40 HP
PARTIES = (
    pa.Party("hero", KNIGHT, (18, 19)),
    pa.Party("target", PEASANT, (19, 19), owner="neutral"),
)
ADDED = 2 * 2 + 1                                   # два отряда по юниту + событие
CHANGED: set[str] = set()


def tables():
    return pa.Tables()


def build(base: sg.Sg, t, parties=PARTIES) -> sg.Sg:
    return uc.end_map_build(base, t, parties, NAME, win=True)


def checks(ch, scen: sg.Sg, base: sg.Sg, t) -> None:
    uc.end_map_checks(ch, scen, base, t, PARTIES, win=True)


def sabotages(base: sg.Sg, t):
    from dataclasses import replace
    moved = (PARTIES[0], replace(PARTIES[1], xy=(21, 19)))
    return [
        ("цель отодвинута от героя", "design", build(base, t, moved).dumps()),
        ("условие «отряд есть» вместо «нет»", "end_event", uc.end_event_sabotage(build(base, t), base)),
        ("событие поражения вместо победы", "end_event",
         uc.end_map_build(base, t, PARTIES, NAME, win=False).dumps()),
    ]


def print_plan(base: sg.Sg, t) -> None:
    for p in PARTIES:
        print(f"  {p.role}: {t.name(p.leader)} ({p.leader}) в {p.xy}, владелец {p.owner}")
    print(f"  Империя {uc.players(base)[uc.EMPIRE]['PLAYER_ID']}: гибель цели -> WIN_SCEN 1")


if __name__ == "__main__":
    sys.exit(uc.run(sys.modules[__name__]))
