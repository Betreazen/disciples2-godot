#!/usr/bin/env python3
r"""Один длинный прогон карты С4: игра открывается ОДИН раз, играет маршрут из route.json,
кладёт журнал зонда и сохранения игры в corpus/<каталог>/.

Время пользователя дорого (Global Constraints плана Ф5), поэтому прогон один и длинный, а не
десять коротких: запуск -> карта -> калибровка -> N ходов -> сохранения в контрольных точках ->
выход. Любая осечка на ходу — снимок, статус route_failed и ОСТАНОВКА: маршрут, доигранный до
места обрыва, годен для харнесса, а сбитая последовательность — нет.

    python tools/corpus_c4_run.py --dry-run --from-file corpus/sample/one_battle.jsonl \
        --route route.json --out corpus/_dry
    python tools/corpus_c4_run.py --route route.json --calibrate --scenario c4_route \
        --out corpus/20260907-c4

Что кладётся в каталог:
    map-001.jsonl        журнал зонда за весь прогон (вне git)
    map-start.sg         СТАРТОВАЯ карта прогона — копия варианта сценария (вне git)
    route.json           копия сыгранного маршрута (вне git)
    save-NN.sg           сохранение игры на контрольной точке, NN — номер хода (вне git)
    save-final.sg        сохранение после последнего хода (вне git)
    manifest.json        под git: sha256 и размеры всего перечисленного, счётчики событий

Манифест — СПИСОК из одной строки того же вида, что у tools/corpus_run.py (file, sha256, bytes,
events, battles, battle_end, scenario, started_at, seconds, status) плюс route/turns_played/saves.
Список, а не объект, ради update_root_manifest(), который обходит строки всех каталогов.
"""
from __future__ import annotations

import argparse
import json
import shutil
import sys
import time
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO_ROOT / "tools"))
import corpus_run as cr  # noqa: E402  — sha256_file/write_json_atomic/summarize_journal/move_journal
import autoplay as ap  # noqa: E402  — путь к gamedrv уже добавлен импортом corpus_run
import goto_battle as gb  # noqa: E402
import ini  # noqa: E402  (tools/gamedrv)
import map_route as mr  # noqa: E402

JOURNAL_NAME = "map-001.jsonl"


def load_route(path: Path) -> dict:
    route = json.loads(path.read_text(encoding="utf-8"))
    for key in ("turns", "checkpoints"):
        if key not in route:
            raise SystemExit("%s: нет поля %r — маршрут строится scenario_c4.py route"
                              % (path.name, key))
    turns = route["turns"]
    if not turns:
        raise SystemExit("%s: маршрут пуст" % path.name)
    numbers = [t["turn"] for t in turns]
    if numbers != list(range(1, len(turns) + 1)):
        raise SystemExit("%s: номера ходов не идут подряд с 1: %s" % (path.name, numbers[:10]))
    bad = [c for c in route["checkpoints"] if c not in numbers]
    if bad:
        raise SystemExit("%s: контрольные точки вне маршрута: %s" % (path.name, bad))
    return route


def save_row(turn: int | str, path: Path) -> dict:
    return {"turn": turn, "file": path.name, "sha256": cr.sha256_file(path),
            "bytes": path.stat().st_size}


def stash_inputs(out_dir: Path, route_path: Path, scenario: str) -> list[dict]:
    """Положить рядом с журналом то, без чего харнесс С4 прогон не прочитает: сыгранный маршрут
    и СТАРТОВУЮ карту.

    Прогон один и длинный (Global Constraints плана Ф5), второго шанса собрать эти файлы не будет,
    а save-NN.sg без стартовой карты сверять не с чем: модель стартует именно с неё. Имя
    map-start.sg выбрано так, чтобы оно шло раньше save-*.sg при сортировке — этим же порядком
    ищет стартовую карту EconomyHarnessTests.
    """
    rows = []
    src = gb.TARGET if scenario in (None, "", "test_fast_battle") else gb.SCENARIOS / ("%s.sg" % scenario)
    if src.exists():
        shutil.copy2(src, out_dir / "map-start.sg")
        rows.append(save_row("start", out_dir / "map-start.sg"))
    else:
        print("ВНИМАНИЕ: стартовой карты %s нет — харнессу С4 будет нечего сверять" % src)
    shutil.copy2(route_path, out_dir / "route.json")
    rows.append(save_row("route", out_dir / "route.json"))
    return rows


def build_manifest(out_dir: Path, dest: Path | None, route_path: Path, route: dict,
                   scenario: str, started_at: str, seconds: float, status: str,
                   turns_played: int, saves: list[dict],
                   inputs: list[dict] | None = None) -> tuple[list[dict], Counter]:
    row, counts = cr.build_row(dest, scenario, started_at, seconds, status)
    row["route"] = {"file": route_path.name, "sha256": cr.sha256_file(route_path),
                    "turns": len(route["turns"]), "checkpoints": route["checkpoints"]}
    row["turns_played"] = turns_played
    row["saves"] = saves
    row["inputs"] = inputs or []
    rows = [row]
    cr.write_json_atomic(out_dir / "manifest.json", rows)
    return rows, counts


def play_route(route: dict, out_dir: Path, scenario: str, calibrate: bool,
               state: dict) -> None:
    """Один запуск игры на весь маршрут.

    Ход и сохранения пишутся в state НА КАЖДОМ шаге, а не возвращаются в конце: при обрыве
    манифест обязан честно сказать, до какого хода маршрут доигран (частичный маршрут годен
    для харнесса до места обрыва).
    """
    saves: list[dict] = state["saves"]
    gb.open_to_map(scenario=scenario, lord="mage")   # пеший герой Archmage: местность должна стоить
    if calibrate:
        mr.calibrate()
    calib = mr.load_calib()
    checkpoints = set(route["checkpoints"])

    for item in route["turns"]:
        turn, target = item["turn"], tuple(item["target"])
        got = mr.play_turn(target, calib)
        print("ход %2d: цель %s, стал %s%s"
              % (turn, target, got, "" if got == target else "  (не дошёл — бюджет хода)"))
        day = mr.end_turn()
        state["turns"] = turn
        if day is not None:
            print("   день по журналу: %d" % day)
        if turn in checkpoints:
            saves.append(save_row(turn, mr.quick_save(out_dir / ("save-%02d.sg" % turn))))
    saves.append(save_row("final", mr.quick_save(out_dir / "save-final.sg")))


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--route", type=Path, required=True, help="route.json из scenario_c4.py route")
    p.add_argument("--out", type=Path, default=None,
                   help="каталог корпуса (default: corpus/<дата>-c4)")
    p.add_argument("--scenario", default="c4_route",
                   help="вариант из game-dev/scenarios/<имя>.sg (default: c4_route)")
    p.add_argument("--calibrate", action="store_true",
                   help="откалибровать «экран -> клетка» в начале прогона (нужно всегда, "
                        "кроме повтора в том же окне)")
    p.add_argument("--resume", action="store_true",
                   help="писать в каталог с уже существующим manifest.json (перезаписывает его). "
                        "Продолжить сам маршрут с середины нельзя: это требовало бы загрузки "
                        "сохранения, чего драйвер не умеет — прогон всегда идёт с хода 1")
    p.add_argument("--dry-run", action="store_true", help="без игры: взять журнал из --from-file")
    p.add_argument("--from-file", type=Path, default=None, help="журнал для --dry-run")
    args = p.parse_args()

    if args.dry_run and args.from_file is None:
        p.error("--dry-run требует --from-file")
    if args.from_file is not None and not args.from_file.exists():
        p.error("нет файла %s" % args.from_file)

    route = load_route(args.route)
    out_dir = args.out or (REPO_ROOT / "corpus" / (datetime.now().strftime("%Y%m%d") + "-c4"))
    out_dir.mkdir(parents=True, exist_ok=True)
    if (out_dir / "manifest.json").exists() and not args.resume:
        p.error("%s/manifest.json уже существует — передайте --resume или другой --out"
                % out_dir)

    print("маршрут %s: ходов %d, контрольные точки %s, сценарий %s"
          % (args.route.name, len(route["turns"]), route["checkpoints"], args.scenario))
    inputs = stash_inputs(out_dir, args.route, args.scenario)

    started_at = datetime.now(timezone.utc).isoformat()
    t0 = time.time()
    dest: Path | None = None
    status = "ok"
    state: dict = {"turns": 0, "saves": []}

    if args.dry_run:
        dest = out_dir / JOURNAL_NAME
        shutil.copy2(args.from_file, dest)   # copy, никогда move: --from-file может быть образцом
        print("--dry-run: журнал %s скопирован как %s, игра не запускалась"
              % (args.from_file.name, dest.name))
    else:
        probe_dir = ap.GAME / "probe"
        ap.kill_leftover_game()
        before = set(probe_dir.glob("*.jsonl")) if probe_dir.is_dir() else set()
        # Подтверждение конца хода даёт лишний диалог с галочкой, который вслепую не нажать
        # (Enter на карте открывает чат): выключаем его на время прогона и возвращаем в finally.
        # Правка байтовая, файл целиком не переписывается — Disciple.ini этого не переживает.
        eot_before = ini.get("Settings", "ConfirmEOT")
        if eot_before is not None and eot_before.strip() != "0":
            ini.set("Settings", "ConfirmEOT", "0")
            print("Disciple.ini: ConfirmEOT %s -> 0 (вернём после прогона)" % eot_before.strip())
        try:
            play_route(route, out_dir, args.scenario, args.calibrate, state)
        except Exception as e:
            status = "route_failed"
            print("СБОЙ на ходу %d: %s" % (state["turns"] + 1, e))
            try:
                ap.screenshot(out_dir / ("fail-turn-%02d.png" % (state["turns"] + 1)))
            except Exception as shot_error:
                print("  снимок при сбое не удался: %s" % shot_error)
        finally:
            try:
                ap.kill_leftover_game()
                gb.use_scenario(None)            # вернуть оригинал в Exports/test_fast_battle.sg
            finally:                             # ini вернуть даже при сбое выше
                if eot_before is not None and eot_before.strip() != "0":
                    ini.set("Settings", "ConfirmEOT", eot_before.strip())
                    print("Disciple.ini: ConfirmEOT возвращён в %s" % eot_before.strip())
        fresh = [q for q in probe_dir.glob("*.jsonl") if q not in before]
        if fresh:
            candidate = out_dir / JOURNAL_NAME
            journal = max(fresh, key=lambda q: q.stat().st_mtime)
            if cr.move_journal(journal, candidate):
                dest = candidate
            else:
                status = "move_failed"
                print("перенос журнала не удался, остаётся в probe/: %s" % journal)
        else:
            status = status if status != "ok" else "no_journal"
            print("новых журналов в %s не появилось — зонд не установлен?" % probe_dir)

    rows, counts = build_manifest(out_dir, dest, args.route, route, args.scenario, started_at,
                                  time.time() - t0, status, state["turns"], state["saves"], inputs)
    row = rows[0]
    print()
    print("статус %s, ходов сыграно %d из %d, сохранений %d, событий в журнале %d"
          % (row["status"], row["turns_played"], len(route["turns"]), len(row["saves"]),
             row["events"]))
    if counts:
        print("по ev: " + " ".join("%s=%d" % (k, v)
                                   for k, v in sorted(counts.items(), key=lambda kv: str(kv[0]))))
    for s in row["saves"]:
        print("  сохранение ход %s: %s %d байт" % (s["turn"], s["file"], s["bytes"]))
    cr.update_root_manifest(REPO_ROOT / "corpus")
    return 0 if row["status"] == "ok" else 1


if __name__ == "__main__":
    sys.exit(main())
