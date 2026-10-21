#!/usr/bin/env python3
"""Итоги реестра расхождений `docs/divergence-ledger.md`: строки по областям и статусам, источники.

    python tools/divergence_ledger_count.py [путь к реестру]

Строка реестра — `| ID | что расходилось | где найдено | статус |` под заголовком `### Область` раздела «Реестр». Статус — первое
слово последней колонки: исправлено / не баг / открыто / не проверено / исключено (договорённость 18.10). Раздел «Карты 13–15» (ID `К…`) считается отдельно и в
итог не входит. Печатает таблицы в разметке Markdown — их и вставлять в раздел «Итог».
"""
from __future__ import annotations

import re
import sys
from collections import Counter
from pathlib import Path

STATUSES = ("исправлено", "не баг", "открыто", "не проверено", "исключено")
ROW = re.compile(r"^\|\s*([А-ЯA-Z][а-яa-z]*\d+)\s*\|(.*)\|\s*$")

# Происхождение по колонке «где найдено»: первый подходящий признак (оценка).
ORIGINS = (
    ("Партии пользователя в порте (пометки F9, слова пользователя)", re.compile(r"партия|пометк|слова пользователя|прогон 1[34]\.10|Хуорн", re.I)),
    ("Харнессы по корпусу и сохранениям", re.compile(r"Harness|харнесс|С1|С2|_check|свободный день|FreeDay|кандидаты|AvCompare|AV §|probe74|§\d", re.I)),
    ("Листинг, аудит, ревью", re.compile(r"листинг|аудит|ревью|REA|wt/port3|r17b/|fix/|port-status|«Не закрыто»|^\s*1[34]\.10\s*$", re.I)),
)
JOURNALS = "Журналы зонда: синтетические карты, корпус оригинала"


def status_of(cell: str) -> str:
    text = cell.strip().lstrip("*").lower()
    for s in STATUSES:
        if text.startswith(s):
            return s
    raise ValueError(f"статус не распознан: {cell.strip()[:60]}")


def origin_of(cell: str) -> str:
    for name, pat in ORIGINS:
        if pat.search(cell):
            return name
    return JOURNALS


def parse(path: Path):
    area, rows, late = None, [], []
    in_ledger = False
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("## "):
            in_ledger = line.strip() == "## Реестр"
            area = "Карты 13–15" if line.startswith("## Карты 13") else None
            continue
        if line.startswith("### ") and in_ledger:
            area = line[4:].strip()
            continue
        m = ROW.match(line)
        if not m or area is None:
            continue
        cells = [c for c in line.strip().strip("|").split("|")]
        if len(cells) != 4:
            raise ValueError(f"{m.group(1)}: ждали 4 колонки, есть {len(cells)}")
        item = (area, m.group(1), status_of(cells[3]), origin_of(cells[2]))
        (late if area == "Карты 13–15" else rows).append(item)
    return rows, late


def main(argv: list[str]) -> int:
    path = Path(argv[1]) if len(argv) > 1 else Path(__file__).resolve().parent.parent / "docs" / "divergence-ledger.md"
    rows, late = parse(path)
    ids = [r[1] for r in rows + late]
    dup = [i for i, n in Counter(ids).items() if n > 1]
    if dup:
        print("повторные ID:", ", ".join(dup), file=sys.stderr)
        return 1
    areas = list(dict.fromkeys(r[0] for r in rows))
    print("| Область | Найдено | Исправлено | Не баг (движок, харнесс) | Открыто | Не проверено журналом | Исключено (договорённость) |")
    print("|---|---|---|---|---|---|---|")
    total = Counter()
    for a in areas:
        c = Counter(r[2] for r in rows if r[0] == a)
        total.update(c)
        print(f"| {a} | {sum(c.values())} | " + " | ".join(str(c[s]) for s in STATUSES) + " |")
    print(f"| **Всего** | **{sum(total.values())}** | " + " | ".join(f"**{total[s]}**" for s in STATUSES) + " |")
    print()
    print("| Где найдено (оценка, первый признак) | Строк |")
    print("|---|---|")
    for name, n in Counter(r[3] for r in rows).most_common():
        print(f"| {name} | {n} |")
    if late:
        lc = Counter(r[2] for r in late)
        print()
        print(f"Карты 13–15 (вне итога): {len(late)} — " + ", ".join(f"{s} {lc[s]}" for s in STATUSES if lc[s]))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
