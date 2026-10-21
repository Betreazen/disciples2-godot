#!/usr/bin/env python3
r"""f7_cli.py — общий каркас генераторов синтетических карт Ф7 (make_f7_mNN_map.py) для НОВЫХ скриптов.

Старые m15–m22 повторяют один скелет: флаги --out-dir/--plan/--selftest/--scratch, честная карта плюс порча файла
по одной, отчёт «ловится / ПРОПУЩЕН» по группам сторожей. Здесь этот скелет один раз; старые скрипты не переводятся
(их карты должны собираться как прежде), новые берут его отсюда:

    import f7_cli as cli
    p = cli.parser(__doc__, out_dir=mp.EXPORTS, source=("--template", TEMPLATE))
    a = p.parse_args(argv)
    if a.selftest:
        cases = cli.file_cases(lambda t: build(a.template, t), verify, sabotage, SABOTAGE, a.scratch, "m23", t)
        return cli.report("m23", [("файл карты", cases)])

Модуль лёгкий: не тянет таблицы игры и соседние генераторы, только стандартную библиотеку.
"""
from __future__ import annotations

import argparse
import contextlib
import io
from collections.abc import Callable, Sequence
from pathlib import Path
from typing import Any

SCRATCH = Path(__file__).resolve().parents[1] / "corpus" / "_f7-selftest"

Case = tuple[str, "str | None"]   # (что испорчено, текст сторожа или None — порча прошла незамеченной)


def raises(fn: Callable[[], object]) -> str | None:
    """Вернуть текст падения или None, если сторож промолчал."""
    try:
        with contextlib.redirect_stdout(io.StringIO()):
            fn()
    except (SystemExit, AssertionError, ValueError) as e:
        return str(e)
    return None


def parser(doc: str | None, out_dir: Path, source: tuple[str, Path] | None = None,
           selftest_help: str = "порча файла плюс сторож") -> argparse.ArgumentParser:
    """Флаги всех генераторов Ф7; source — свой входной файл скрипта, например ("--template", TEMPLATE)."""
    p = argparse.ArgumentParser(description=doc, formatter_class=argparse.RawDescriptionHelpFormatter)
    if source is not None:
        p.add_argument(source[0], type=Path, default=source[1])
    p.add_argument("--out-dir", type=Path, default=out_dir)
    p.add_argument("--plan", action="store_true", help="только расчёт, ничего не записывать")
    p.add_argument("--selftest", action="store_true", help=selftest_help)
    p.add_argument("--scratch", type=Path, default=SCRATCH)
    return p


def file_cases(build: Callable[[Any], Any], verify: Callable[..., None], sabotage: Callable[[Any, str, Any], Any],
               sabotages: Sequence[tuple[str, str]], scratch: Path, key: str, tables: Any) -> list[Case]:
    """Честная карта проходит собственную проверку; затем каждая порча по одной — на месте той же карты.

    build(tables) — карта с методом write(path); verify(path, tables, quiet=True) падает на нарушении;
    sabotage(карта, что, tables) — испорченная копия."""
    scratch.mkdir(parents=True, exist_ok=True)
    out = scratch / f"f7_{key}_selftest.sg"
    scen = build(tables)
    scen.write(out)
    try:
        honest = raises(lambda: verify(out, tables, quiet=True))
        if honest is not None:
            raise SystemExit(f"честная карта {key} не проходит собственную проверку: {honest}")
        cases: list[Case] = []
        for what, title in sabotages:
            sabotage(scen, what, tables).write(out)
            cases.append(("файл: " + title, raises(lambda: verify(out, tables, quiet=True))))
        return cases
    finally:
        out.unlink(missing_ok=True)


def report(key: str, groups: Sequence[tuple[str, Sequence[Case]]], width: int = 44) -> int:
    """Печать «ловится / ПРОПУЩЕН» по группам; 1, если хоть одна порча прошла незамеченной. Пустая группа — не проверка."""
    bad = total = 0
    for title, cases in groups:
        if not cases:
            raise SystemExit(f"группа «{title}» не сделала ни одного сравнения — это не проверка")
        print(f"--- {title}: {len(cases)} сравнений")
        for name, msg in cases:
            print(f"{name:<{width}} " + (("ловится: " + msg.split("\n")[0]) if msg else "ПРОПУЩЕН"))
            bad += msg is None
        total += len(cases)
    if not total:
        raise SystemExit(f"selftest {key} не сделал ни одного сравнения — это не проверка")
    print(f"selftest {key}: сработали {total - bad} сторожей из {total} сравнений")
    return 1 if bad else 0
