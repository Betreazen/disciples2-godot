#!/usr/bin/env python3
"""Патчер СОХРАНЕНИЯ прогона Ф6.

    python tools/patch_f6_save.py --save "<game-dev>/SaveGame/QuickSave.sg"
    python tools/patch_f6_save.py --restore
    python tools/patch_f6_save.py selftest      # без игры: на сохранении прогона Ф5

**Зачем понадобился.** Задать в файле СЦЕНАРИЯ построенное состояние столицы и готовый отряд
нельзя в принципе: при старте сценария игра пересоздаёт игрока по выбранному лорду
(`docs/fidelity-f5.md` §5 п. 8 — стартовый герой `S143UN0001` -> `S143UN0002`, лорд
`G000LR0001` -> `G000LR0003`), и вместе с игроком стираются и `CPlayerBuildings`, и солдаты
стартового отряда. Измерено дважды 08.09.2026: в окне построек живой игры были только здания
с `Gbuild.REQUIRED = g000000000`, а герой стоял один при «Лидерство 0/3» — хотя в
`f6_route.sg` стек `S143KC0000` держит все шесть юнитов, и разбор файла это подтверждает.

**В сохранении игрок уже создан.** Пересоздания при загрузке нет, поэтому патчится не карта, а
сохранение: пользователь один раз загружает сценарий, выбирает лорда и жмёт `Q`, дальше правит
скрипт. Формат тот же `.sg`, поэтому тут переиспользуются те же `sgfile` и те же помощники
`make_f6_map`, ни одного нового разбора.

**Целевая постройка прогона в сохранение НЕ дописывается.** Крюки `build_status` и `hire_list`
стоят с Ф5 и ни разу не срабатывали — прогону нужен сам факт постройки и найма.

**Профилей Ф7 здесь больше нет.** С 09.09.2026 карты M4, M6, M8, M9 и M10 задают отряд, вещи
и заклинания СЦЕНАРИЕМ (`tools/make_f7_patch_maps.py`). Заплата их не довела ни разу: она
требовала ручного `Q` и ручной команды между загрузкой и игрой, и за весь прогон
`corpus/20260909-passive` `QuickSave.sg` не переписывался вовсе, а на лордах Воителя и Мага
она бы ещё и отказалась — стартовый отряд там из четырёх юнитов. Оставлять мёртвый ключ
`--profile` не стали: он падает с подсказкой, куда идти.
"""
from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402
import make_f6_map as f6  # noqa: E402
import scenario_squads as sq  # noqa: E402

# Файл, который берёт штатный путь загрузки `gamedrv/goto_map.py` («Одиночная игра» ->
# «Загрузить» -> автосохранение). Как и в `goto_battle.use_scenario`, меняется не строка списка
# (её выбирают вслепую), а содержимое файла; оригинал сохраняется один раз рядом как `.orig`.
SAVEGAME = f6.MODDING / "game-dev" / "SaveGame"
AUTOSAVE = SAVEGAME / "Автосохранение.sg"
QUICKSAVE = SAVEGAME / "QuickSave.sg"

# Пробная постройка: 200 золота, `REQUIRED = g000000000`, ветка рыцарей (`BRANCH 0`) — та,
# в которую прогон не заходит, поэтому целевой Библиотеке она не мешает. Смысл ровно один:
# первое же сохранение прогона ответит числом на открытый вопрос «переживает ли правленый
# `CPlayerBuildings` загрузку сохранения». Если переживает — будущие прогоны могут задавать
# построенную столицу; если нет — прогон всё равно доедет, потому что цель от неё не зависит.
PROBE_BUILDING = "G000BB0001"       # Stable / Конюшня


def player_stack(scen: sg.Sg) -> sg.Record:
    """Единственный стек игрока-человека."""
    owner = f6.human_player(scen)["PLAYER_ID"]
    own = [r for r in scen.find(sg.WHAT_STACK) if sg.parse_stack(r.body)["OWNER"] == owner]
    if len(own) != 1:
        raise SystemExit("стеков игрока %d, ожидался один" % len(own))
    return own[0]


def squad_types(scen: sg.Sg, stack: sg.Record) -> list[str]:
    types = {r.obj_id.upper(): sg.fields(r.body, sg.LAYOUT_UNIT)["TYPE"].lower()
             for r in scen.find(sg.WHAT_UNIT)}
    st = sg.parse_stack(stack.body)
    return [types[st[f"UNIT_{k}"].upper()] for k in range(6) if st[f"UNIT_{k}"] != sg.NULL_ID]


def leadership(scen: sg.Sg, stack: sg.Record, t: sq.Tables) -> int:
    """Лидерство лидера стека из `Gunits` — потолок числа солдат рядом с ним."""
    lead_type = squad_types(scen, stack)[0]
    return int(t.unit(lead_type)["LEADERSHIP"] or 0)


def patch(save: Path) -> tuple[sg.Sg, dict]:
    t = sq.Tables()
    scen = sg.Sg.read(save)
    stack = player_stack(scen)
    have = squad_types(scen, stack)
    want = [u for u in f6.SQUAD if u not in have]
    lead = leadership(scen, stack, t)
    if want:
        scen, _ = sq.add_soldiers(scen, stack, want, scen.next_ids("UN", len(want)), t)

    player = f6.human_player(scen)
    builds = [b.upper() for b in f6.read_list(scen, player["BUILDS_ID"], "BUILD_ID")]
    builds = builds + [PROBE_BUILDING] if PROBE_BUILDING not in builds else builds
    if f6.TARGET_BUILDING.upper() in builds:
        raise SystemExit("целевая постройка %s уже стоит — прогону нечего строить"
                         % f6.TARGET_BUILDING)
    scen = f6.set_bank(scen, f6.BANK)
    scen = f6.set_list_record(scen, player["BUILDS_ID"], "BUILD_ID", builds)
    scen = f6.set_list_record(scen, player["KNOWN_ID"], "SPELL_ID", f6.KNOWN_SPELLS)
    return scen, {"лидер": have[0], "лидерство": lead, "дописано солдат": len(want),
                  "постройки": builds, "заклинаний": len(f6.KNOWN_SPELLS)}


def verify(path: Path) -> None:
    """Прочитать записанное обратно и проверить числами всё, что патчер обещал."""
    scen = sg.Sg.read(path)              # чтение проверяет счётчик объектов и рамки всех записей
    t = sq.Tables()
    stack = player_stack(scen)
    squad = squad_types(scen, stack)[1:]
    missing = [u for u in f6.SQUAD if u not in squad]
    assert not missing, f"в отряде нет {missing} (есть {squad})"
    player = f6.human_player(scen)
    assert player["BANK"] == f6.BANK, player["BANK"]
    builds = [b.upper() for b in f6.read_list(scen, player["BUILDS_ID"], "BUILD_ID")]
    assert PROBE_BUILDING in builds, builds
    assert f6.TARGET_BUILDING.upper() not in builds, "целевая постройка уже стоит"
    known = f6.read_list(scen, player["KNOWN_ID"], "SPELL_ID")
    assert known == f6.KNOWN_SPELLS, known
    lead = leadership(scen, stack, t)
    print("проверка чтения: отряд %d + лидер, казна, постройки %s, заклинаний %d — сошлись"
          % (len(squad), builds, len(known)))
    if len(squad) > lead:
        # ponytail: лидерство героя ниже размера вынужденного отряда, и поднять его нечем —
        # предметов с модификатором Leadership в GItem нет, а лидеры с лидерством 5+ это стражи
        # (`UNIT_CAT 8`, 900 HP). Потолок снимается первым же шагом прогона `verify_squad`:
        # он сверяет состав ПОСЛЕ загрузки и останавливает прогон, если движок отряд урезал.
        print("ВНИМАНИЕ: солдат %d при лидерстве %d — переживёт ли состав загрузку, "
              "снимает шаг verify_squad на первом ходу" % (len(squad), lead))



def restore() -> int:
    orig = AUTOSAVE.with_name(AUTOSAVE.name + ".orig")
    if not orig.exists():
        print("оригинала %s нет — восстанавливать нечего" % orig.name)
        return 0
    shutil.copy2(orig, AUTOSAVE)
    print("%s возвращён из %s" % (AUTOSAVE.name, orig.name))
    return 0


def selftest() -> int:
    """Без игры: патч сохранения прогона Ф5 в scratchpad и разбор результата."""
    src = Path(__file__).resolve().parents[1] / "corpus" / "20260907-c4" / "save-10.sg"
    if not src.exists():
        raise SystemExit("нет сохранения Ф5 %s — selftest не на чем прогнать" % src)
    out = Path(__file__).resolve().parents[1] / "corpus" / "_selftest-f6-save.sg"
    scen, stats = patch(src)
    scen.write(out)
    print("selftest на %s: %s" % (src.name, ", ".join(f"{k} {v}" for k, v in stats.items())))
    verify(out)
    out.unlink()
    return 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("cmd", nargs="?", default="patch", choices=["patch", "selftest"])
    p.add_argument("--save", type=Path, default=QUICKSAVE,
                   help="исходное сохранение пользователя (default: SaveGame/QuickSave.sg)")
    p.add_argument("--out", type=Path, default=AUTOSAVE,
                   help="куда положить патченое (default: SaveGame/Автосохранение.sg — его и "
                        "грузит goto_map.py)")
    p.add_argument("--restore", action="store_true", help="вернуть исходное автосохранение")
    # Надгробие удалённых профилей Ф7: ключ остался только затем, чтобы старая памятка
    # падала с адресом, а не с невнятным «unrecognized arguments».
    p.add_argument("--profile", help=argparse.SUPPRESS)
    a = p.parse_args(argv)
    if a.profile:
        raise SystemExit("профилей Ф7 у заплаты больше нет: карты M4, M6, M8, M9 и M10 задают "
                         "отряд, вещи и заклинания сценарием — соберите карту заново командой "
                         "`python tools/make_f7_patch_maps.py --only %s` и просто загрузите её"
                         % a.profile)
    if a.restore:
        return restore()
    if a.cmd == "selftest":
        return selftest()
    if not a.save.exists():
        raise SystemExit("нет сохранения %s — сначала загрузите сценарий и нажмите Q" % a.save)
    scen, stats = patch(a.save)
    orig = a.out.with_name(a.out.name + ".orig")
    if a.out.exists() and not orig.exists():
        shutil.copy2(a.out, orig)
    a.out.parent.mkdir(parents=True, exist_ok=True)
    scen.write(a.out)
    print("%s: %s" % (a.out, ", ".join(f"{k} {v}" for k, v in stats.items())))
    verify(a.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
