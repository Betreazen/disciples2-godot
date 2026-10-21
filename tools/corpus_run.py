#!/usr/bin/env python3
r"""Снять корпус боевых журналов зонда: N раз довести игру до боя, дождаться конца,
перенести журнал в corpus/<out>/battle-NNN.jsonl, дописать manifest.json.

Предпосылка (не проверяется и не выполняется этим скриптом — готовится заранее):
game-dev должен уже иметь установленный зонд (``python tools/d2probe/install.py``, БЕЗ
``--autobattle``): бой доигрывается кнопкой «Быстрый поединок» самой игры
(goto_battle.press_fast_battle). Маркер probe/autobattle годится только для формул — под ним
сторона человека не ходит и очередь ходов искажена (см. docs/corpus-schema.md); если маркер
стоит, кнопка не нажимается. ini (BattleSpeed, AutoSave и т.п.) этот
скрипт не трогает — правки только через tools/gamedrv/ini.py, отдельно.

    python tools/corpus_run.py --count 5
    python tools/corpus_run.py --count 50 --resume --out corpus/20260906
    python tools/corpus_run.py --dry-run --from-file corpus/sample/one_battle.jsonl --out /tmp/x

По умолчанию (--keep-game) игра запускается один раз на серию: убить остаток игры -> запомнить
*.jsonl в probe/ -> goto_battle.open_to_battle(boot=True) -> press_fast_battle -> ждать в журнале
процесса (один файл на процесс, несколько боёв) прирост числа строк battle_end (опрос раз в 2 с,
--timeout-battle секунд) -> --settle секунд ждать, что файл перестал расти (ok_unsettled, если не
дождались) -> goto_battle.leave_battle() (Esc, Esc, Q, Enter — главное меню) -> следующий бой
open_to_battle(boot=False). Сбой навигации или таймаут -> снимок out/fail-NNN.png, игра убита,
журнал процесса перенесён, серия продолжается новым процессом. По концу серии (или Ctrl+C)
журнал переносится в corpus/<out>/battle-NNN.jsonl ОДНИМ файлом — CorpusReader.Battles режет его
на бои сам; в manifest.json строка на файл, "battles" = число battle_end в нём.

--restart-each — прежнее поведение: перезапуск игры на каждый бой, файл на бой.

В конце — сводная таблица и суммарные счётчики по ev, и пересборка корневого corpus/manifest.json.

--dry-run пропускает игру целиком: копирует --from-file как один журнал через тот же код
переноса+манифеста+счётчиков — так проверяется вся логика без запуска Disciples 2.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import sys
import time
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
MODDING_ROOT = REPO_ROOT.parent / "disciples-modding"
GAMEDRV = MODDING_ROOT / "tools" / "gamedrv"
sys.path.insert(0, str(GAMEDRV))
import autoplay as ap  # type: ignore[import-not-found]  # noqa: E402
import goto_battle as gb  # type: ignore[import-not-found]  # noqa: E402

BATTLE_NAME_RE = re.compile(r"^battle-(\d+)\.jsonl$")
POLL_S = 2.0


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def write_json_atomic(path: Path, data: object) -> None:
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    tmp.replace(path)


def summarize_journal(path: Path) -> tuple[Counter, int]:
    """Счётчики по ev и общее число строк (правило: рядом с фильтром — общий счётчик)."""
    counts: Counter = Counter()
    lines = 0
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if not line:
            continue
        lines += 1
        try:
            ev = json.loads(line).get("ev")
        except json.JSONDecodeError:
            ev = None
        counts[ev] += 1
    return counts, lines


def load_existing_rows(out_dir: Path) -> list[dict]:
    mpath = out_dir / "manifest.json"
    if not mpath.exists():
        return []
    return json.loads(mpath.read_text(encoding="utf-8"))


def next_index(rows: list[dict]) -> int:
    nums = [int(m.group(1)) for r in rows if r.get("file")
             for m in [BATTLE_NAME_RE.match(r["file"])] if m]
    return max(nums, default=0) + 1


def build_row(dest: Path | None, scenario: str, started_at: str, seconds: float,
              status: str) -> tuple[dict, Counter]:
    if dest is None:
        row = {"file": None, "sha256": None, "bytes": 0, "events": 0, "battles": 0,
               "battle_end": False, "scenario": scenario, "started_at": started_at,
               "seconds": round(seconds, 1), "status": status}
        return row, Counter()
    counts, lines = summarize_journal(dest)
    row = {
        "file": dest.name,
        "sha256": sha256_file(dest),
        "bytes": dest.stat().st_size,
        "events": lines,
        "battles": counts.get("battle_end", 0),  # доигранные бои (battle_start идёт на каждую копию BattleMsgData)
        "battle_end": counts.get("battle_end", 0) > 0,
        "scenario": scenario,
        "started_at": started_at,
        "seconds": round(seconds, 1),
        "status": status,
    }
    return row, counts


def count_ends(journal: Path) -> int:
    try:
        return journal.read_bytes().count(b'"ev":"battle_end"')
    except OSError:
        return 0


def wait_for_battle(probe_dir: Path, before: set[Path], timeout_s: float,
                    journal: Path | None = None, ends_before: int = 0) -> Path | None:
    """Ждать battle_end: journal=None — в новом *.jsonl probe_dir (первый бой процесса), иначе —
    (ends_before+1)-ю строку battle_end в известном журнале (--keep-game: один файл на процесс).
    --timeout-battle действует только до этого момента — что происходит после battle_end, решает
    settle_journal() отдельным бюджетом (--settle), см. её docstring."""
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        if journal is None and probe_dir.is_dir():
            found = [p for p in probe_dir.glob("*.jsonl") if p not in before]
            if found:
                if len(found) > 1:
                    print(f"  внимание: {len(found)} новых журналов в probe/, беру последний по mtime")
                journal = max(found, key=lambda p: p.stat().st_mtime)
        if journal is not None and count_ends(journal) > ends_before:
            return journal
        time.sleep(POLL_S)
    return None  # таймаут: журнал мог появиться, но нужный battle_end в нём так и не встретился


def settle_journal(journal: Path, settle_s: float) -> bool:
    """После первого battle_end движок может ещё дописывать строки (dmg_max/power из
    подсказок интерфейса на экране итогов) — отдельный короткий предел, НЕ откусывающий от
    --timeout-battle: иначе бой, реально доигравший до конца, терялся бы как timeout из-за
    хвоста дозаписи. True — размер файла не вырос между двумя опросами подряд (2 с). False —
    settle_s исчерпан раньше, чем рост прекратился; журнал всё равно годный и возвращается
    вызывающим (kill_leftover_game дальше по коду может обрубить последнюю строку) — на это
    указывает статус итерации ok_unsettled в манифесте."""
    deadline = time.time() + settle_s
    try:
        pending_size = journal.stat().st_size
    except OSError:
        pending_size = -1
    while time.time() < deadline:
        time.sleep(POLL_S)
        try:
            size = journal.stat().st_size
        except OSError:
            size = -1
        if size == pending_size:
            return True
        pending_size = size
    return False


def run_one(i: int, out_dir: Path, probe_dir: Path, timeout_s: float, settle_s: float,
            scenario: str, fast_key: bool = False) -> tuple[dict, Counter]:
    ap.kill_leftover_game()
    before = set(probe_dir.glob("*.jsonl")) if probe_dir.is_dir() else set()
    started_at = datetime.now(timezone.utc).isoformat()
    t0 = time.time()
    status = "ok"
    journal: Path | None = None
    try:
        gb.open_to_battle(scenario=scenario)
        if not (probe_dir / "autobattle").exists():
            gb.press_fast_battle(use_key=fast_key)  # без маркера бой доигрывает сама игра («Быстрый поединок»)
    except Exception as e:  # boot/nav failure -- log and move on, don't abort the batch
        status = "boot_failed"
        print(f"  [{i}] сбой навигации: {e}")
    else:
        journal = wait_for_battle(probe_dir, before, timeout_s)
        if journal is None:
            status = "timeout"
            try:
                ap.screenshot(out_dir / f"fail-{i:03d}.png")
            except Exception as e:
                print(f"  [{i}] снимок при таймауте не удался: {e}")
        elif not settle_journal(journal, settle_s):
            status = "ok_unsettled"
    ap.kill_leftover_game()
    seconds = time.time() - t0

    dest = None
    if journal is not None:
        candidate = out_dir / f"battle-{i:03d}.jsonl"
        if move_journal(journal, candidate):
            dest = candidate
        else:
            status = "move_failed"
            print(f"  [{i}] перенос журнала не удался, остаётся в probe/: {journal}")
    return build_row(dest, scenario, started_at, seconds, status)


def move_journal(src: Path, dest: Path, attempts: int = 5, pause: float = 0.5) -> bool:
    """До attempts попыток shutil.move: PermissionError (хэндл только что убитого процесса,
    антивирус) не должен обрывать ночной прогон. False — журнал остаётся в probe/ как есть;
    следующая итерация увидит его в своём "before" и не примет за новый бой."""
    for attempt in range(attempts):
        try:
            shutil.move(str(src), str(dest))
            return True
        except OSError:
            if attempt == attempts - 1:
                return False
            time.sleep(pause)
    return False


def run_keep_game(count: int, out_dir: Path, probe_dir: Path, timeout_s: float, settle_s: float,
                  scenario: str, fast_key: bool, rows: list[dict], total_counts: Counter) -> None:
    """--keep-game: один процесс на серию, между боями leave_battle(); журнал процесса — один
    файл, переносится при закрытии процесса (конец серии, сбой, Ctrl+C) и даёт одну строку
    манифеста. Сбой -> снимок, kill, следующий бой стартует новым процессом."""
    game_up = False
    journal: Path | None = None
    before: set[Path] = set()
    started_at, t0, ends, status = "", 0.0, 0, "ok"

    def close_process(final_status: str) -> None:
        nonlocal game_up, journal
        ap.kill_leftover_game()
        game_up = False
        dest = None
        if journal is not None:
            candidate = out_dir / f"battle-{next_index(rows):03d}.jsonl"
            if move_journal(journal, candidate):
                dest = candidate
            else:
                final_status = "move_failed"
                print(f"  перенос журнала не удался, остаётся в probe/: {journal}")
        journal = None
        row, counts = build_row(dest, scenario, started_at, time.time() - t0, final_status)
        rows.append(row)
        total_counts.update(counts)
        write_json_atomic(out_dir / "manifest.json", rows)  # атомарно на каждый файл
        print(f"  => {row['file']} {row['status']} battles={row['battles']} events={row['events']} "
              f"{row['seconds']}s")

    try:
        for n in range(count):
            print(f"[{n + 1}/{count}] бой...")
            tb = time.time()
            if not game_up:
                ap.kill_leftover_game()
                before = set(probe_dir.glob("*.jsonl")) if probe_dir.is_dir() else set()
                started_at, t0, ends, status = datetime.now(timezone.utc).isoformat(), time.time(), 0, "ok"
            fail = None
            try:
                gb.open_to_battle(scenario=scenario, boot=not game_up)
                game_up = True
                if not (probe_dir / "autobattle").exists():
                    gb.press_fast_battle(use_key=fast_key)
                found = wait_for_battle(probe_dir, before, timeout_s, journal, ends)
                if found is None:
                    fail = "timeout"
                else:
                    journal, ends = found, ends + 1
                    if not settle_journal(journal, settle_s):
                        status = "ok_unsettled"
                    gb.leave_battle()
            except Exception as e:  # сбой навигации — лог, снимок, перезапуск; серия не обрывается
                fail = "nav_failed" if game_up else "boot_failed"
                print(f"  сбой навигации: {e}")
            if fail is None:
                print(f"  -> бой {ends} в журнале, {time.time() - tb:.1f}s")
                continue
            try:
                ap.screenshot(out_dir / f"fail-{len(rows) + 1:03d}.png")
            except Exception as e:
                print(f"  снимок при сбое не удался: {e}")
            close_process(fail)
    except KeyboardInterrupt:
        close_process("interrupted")
        raise
    if game_up or journal is not None:
        close_process(status)


def run_dry(from_file: Path, out_dir: Path, scenario: str, idx: int) -> tuple[dict, Counter]:
    dest = out_dir / f"battle-{idx:03d}.jsonl"
    shutil.copy2(from_file, dest)  # copy, never move -- from_file may be the committed sample
    row, counts = build_row(dest, scenario, datetime.now(timezone.utc).isoformat(), 0.0, "ok")
    return row, counts


def print_summary(rows: list[dict], total_counts: Counter) -> None:
    print(f"{'#':>3}  {'file':<20}{'status':<12}{'battles':>8}{'events':>8}{'battle_end':>12}{'seconds':>9}")
    for idx, r in enumerate(rows, 1):
        print(f"{idx:>3}  {str(r['file']):<20}{r['status']:<12}{r['battles']:>8}{r['events']:>8}"
              f"{str(r['battle_end']):>12}{r['seconds']:>9}")
    by_status = Counter(r["status"] for r in rows)
    print(f"итого: файлов={len(rows)} "
          + " ".join(f"{k}={v}" for k, v in sorted(by_status.items())))
    print("по ev: " + " ".join(f"{k}={v}" for k, v in sorted(total_counts.items(), key=lambda kv: str(kv[0]))))


def update_root_manifest(corpus_root: Path) -> None:
    """corpus/manifest.json — сводка по всем каталогам-подкаталогам (кроме sample/)."""
    runs = []
    totals: Counter = Counter()
    for sub in sorted(p for p in corpus_root.iterdir() if p.is_dir() and p.name != "sample"):
        mpath = sub / "manifest.json"
        if not mpath.exists():
            continue
        rows = json.loads(mpath.read_text(encoding="utf-8"))
        battles = sum(r.get("battles", 0) for r in rows)
        events = sum(r.get("events", 0) for r in rows)
        battle_end = sum(1 for r in rows if r.get("battle_end"))
        by_status = Counter(r.get("status") for r in rows)
        runs.append({"dir": sub.name, "files": len(rows), "battles": battles,
                      "events": events, "battle_end": battle_end, "status": dict(by_status)})
        totals["dirs"] += 1
        totals["files"] += len(rows)
        totals["battles"] += battles
        totals["events"] += events
        totals["battle_end"] += battle_end
    write_json_atomic(corpus_root / "manifest.json", {"runs": runs, "totals": dict(totals)})


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                      formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--count", type=int, default=1, help="сколько итераций снять (default: 1)")
    parser.add_argument("--resume", action="store_true", help="продолжить существующий манифест")
    parser.add_argument("--out", type=Path, default=None,
                         help="каталог вывода (default: corpus/<сегодняшняя дата>)")
    parser.add_argument("--timeout-battle", type=float, default=600.0,
                         help="таймаут ожидания battle_end на итерацию, сек (default: 600)")
    parser.add_argument("--settle", type=float, default=10.0,
                         help="отдельный предел стабилизации файла после battle_end, сек "
                              "(default: 10); не входит в --timeout-battle")
    parser.add_argument("--scenario", default="test_fast_battle",
                         help="вариант сценария: подставляется в Exports/test_fast_battle.sg "
                              "из game-dev/scenarios/<имя>.sg и пишется в manifest.json; "
                              "оригинал восстанавливается по завершении")
    parser.add_argument("--fast-key", action="store_true",
                        help="«Быстрый поединок» хоткеем I вместо клика по кнопке (docs/game-hotkeys.md)")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--keep-game", dest="keep_game", action="store_true", default=True,
                      help="(по умолчанию) один процесс игры на серию, между боями выход в главное "
                           "меню; журнал процесса — один файл battle-NNN.jsonl с несколькими боями")
    mode.add_argument("--restart-each", dest="keep_game", action="store_false",
                      help="прежнее поведение: перезапуск игры на каждый бой, файл на бой")
    parser.add_argument("--dry-run", action="store_true",
                         help="без игры: взять готовый журнал из --from-file")
    parser.add_argument("--from-file", type=Path, default=None,
                         help="журнал для --dry-run")
    args = parser.parse_args()

    if args.dry_run and args.from_file is None:
        parser.error("--dry-run требует --from-file")
    if args.from_file is not None and not args.from_file.exists():
        parser.error(f"нет файла {args.from_file}")

    out_dir = args.out or (REPO_ROOT / "corpus" / datetime.now().strftime("%Y%m%d"))
    out_dir.mkdir(parents=True, exist_ok=True)

    rows = load_existing_rows(out_dir) if args.resume else []
    if not args.resume and (out_dir / "manifest.json").exists():
        parser.error(f"{out_dir}/manifest.json уже существует — передайте --resume "
                      "или другой --out, чтобы не потерять его")

    idx = next_index(rows)
    total_counts: Counter = Counter()
    for r in rows:  # учесть уже снятые файлы в сводке по ev при --resume
        f = out_dir / str(r["file"]) if r.get("file") else None
        if f and f.exists():
            c, _ = summarize_journal(f)
            total_counts.update(c)

    if args.dry_run:
        assert args.from_file is not None  # гарантировано отказом parser.error выше
        row, counts = run_dry(args.from_file, out_dir, args.scenario, idx)
        rows.append(row)
        total_counts.update(counts)
        write_json_atomic(out_dir / "manifest.json", rows)
    else:
        probe_dir = ap.GAME / "probe"
        try:
            if args.keep_game:
                run_keep_game(args.count, out_dir, probe_dir, args.timeout_battle, args.settle,
                              args.scenario, args.fast_key, rows, total_counts)
            else:
                for n in range(args.count):
                    i = idx + n
                    print(f"[{i}] бой...")
                    row, counts = run_one(i, out_dir, probe_dir, args.timeout_battle, args.settle,
                                          args.scenario, args.fast_key)
                    rows.append(row)
                    total_counts.update(counts)
                    write_json_atomic(out_dir / "manifest.json", rows)  # атомарно после каждой итерации
                    print(f"  -> {row['status']} battles={row['battles']} events={row['events']} "
                          f"battle_end={row['battle_end']} {row['seconds']}s")
        finally:
            if args.scenario not in (None, "", "test_fast_battle"):
                gb.use_scenario(None)  # вернуть оригинал в Exports/test_fast_battle.sg

    print()
    print_summary(rows, total_counts)
    update_root_manifest(REPO_ROOT / "corpus")
    return 0


if __name__ == "__main__":
    sys.exit(main())
