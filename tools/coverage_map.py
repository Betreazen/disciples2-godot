#!/usr/bin/env python3
r"""Карта «покрытие оригинала → сверено портом»: функции `Discipl2.exe`, исполненные партиями с покрытием (`*.cov.txt`, зонд 7.5),
против адресов, на которые ссылаются порт, харнессы и документы.

    py tools/coverage_map.py <каталог или .cov.txt> [...] --exe ../disciples-modding/game-pristine/Discipl2.exe \
        [--out docs/coverage-map.md] [--json .cache/probe-coverage/map.json]

Функция (надёжное начало `tools/d2probe/coverage/functions.tsv`, `safe` = 1) получает:
- «исполнено» — её начало есть хотя бы в одном `.cov.txt`;
- «харнесс» — адрес внутри неё (от её начала до следующего начала описи) стоит в тесте (`src/*.Tests`, `tests/`);
- «порт» — адрес стоит в коде порта (`src/D2.Data`, `src/D2.Rules`, `godot/`), но не в тесте: поведение перенесено по листингу и
  проверяется харнессами области по журналам (С1/С2, ходы ИИ …), а не по адресу;
- «доки» — адрес только в `docs/` (разбор, реестр);
- «класс» — адреса нет, но класс-хозяин (`functions.tsv`) назван в коде порта или тестах: класс перенесён целиком.

Адрес в середине функции относится к ней: опись `functions.tsv` — начала Ghidra, конец функции — следующее начало. Это оценка
сверху для «харнесса»/«порта» (адрес вызова внутри большой функции засчитывает всю функцию) и снизу для «класса».

Для неисполненных групп печатаются исполненные вызывающие (прямой `call rel32` из исполненной функции другого класса — место, где
партия дошла до развилки и не свернула); для исполненных — событие журнала партии (<журнал>.jsonl рядом с <журнал>.cov.txt), на
котором функция впервые исполнилась: бой, ход ИИ, ход человека или запуск.
"""
from __future__ import annotations

import argparse
import bisect
import collections
import json
import re
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools"))

from port_status_count import AREAS  # noqa: E402
from probe_coverage import DISPLAY, FUNCTIONS, LOGIC, cov_files  # noqa: E402

ADDR = re.compile(r"\b0x([0-9a-fA-F]{6})\b")
PORT_DIRS = ("src/D2.Data", "src/D2.Rules", "godot")
TEST_DIRS = ("src/D2.Data.Tests", "src/D2.Rules.Tests", "tests")
DOC_DIRS = ("docs",)
# Сама карта и её разбор не считаются «доками» адресов (иначе списки неисполненных групп засчитали бы себя).
SKIP_PARTS = {"bin", "obj", ".godot", "graphify-out", "coverage-map.md", "20261019b-cov.md"}
LEVELS = ("harness", "port", "docs", "class", "enum", "none")
LEVEL_RU = {"harness": "харнесс", "port": "порт", "docs": "доки", "class": "класс", "enum": "перечисление", "none": "нигде"}
# Перечисления движка из таблиц (`LAttackClass`, `LRaceCategory` … и их `CEnumConstant`): конструкторы констант при запуске и
# сравнения; в порту это перечисления и данные таблиц, а не код.
ENUM = re.compile(r"^(L[A-Z]\w+|\?\$CEnumConstant@VL\w+)$")
BIG_GROUP = 5


def load_all_functions(path: Path) -> list[tuple[int, str, str, bool]]:
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line or line.startswith("#"):
            continue
        addr, area, cls, safe = (line.split("\t") + ["", "", "", "0"])[:4]
        rows.append((int(addr, 16), area, cls, safe.strip() == "1"))
    rows.sort()
    return rows


def read_hits(path: Path) -> dict[int, int]:
    """Исполненные начала файла `.cov.txt` → seq журнала первого исполнения (0 — нет поля)."""
    hits: dict[int, int] = {}
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if not line or line.startswith("#"):
            continue
        parts = line.split()
        hits[int(parts[0], 16)] = int(parts[3]) if len(parts) > 3 else 0
    return hits


def source_files(dirs: tuple[str, ...], exts: tuple[str, ...]) -> list[Path]:
    out = []
    for d in dirs:
        base = ROOT / d
        if not base.is_dir():
            continue
        for p in base.rglob("*"):
            if p.suffix in exts and p.is_file() and not SKIP_PARTS & set(p.relative_to(ROOT).parts) \
                    and p.name not in SKIP_PARTS:
                out.append(p)
    return out


def referenced(files: list[Path], starts: list[int], lo: int, hi: int) -> tuple[set[int], set[str]]:
    """Функции, внутрь которых указывает адрес из файлов, и весь текст (для поиска имён классов)."""
    funcs: set[int] = set()
    texts = []
    for p in files:
        text = p.read_text(encoding="utf-8", errors="replace")
        texts.append(text)
        for m in ADDR.finditer(text):
            a = int(m.group(1), 16)
            if lo <= a < hi:
                i = bisect.bisect_right(starts, a) - 1
                if i >= 0:
                    funcs.add(starts[i])
    return funcs, set(re.findall(r"\b[A-Z][A-Za-z0-9_]{3,}\b", "\n".join(texts)))


def call_graph(exe: Path, starts: list[int]) -> tuple[dict[int, set[int]], int, int]:
    """Прямые вызовы `E8 rel32` в `.text`: цель (начало описи) → множество функций-вызывающих. Линейный проход по байтам даёт и
    ложные `E8` внутри операндов; цель обязана быть началом описи, этого для оценки «кто зовёт» достаточно."""
    import pefile

    pe = pefile.PE(str(exe), fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    text = next(s for s in pe.sections if s.Name.rstrip(b"\0") == b".text")
    lo = base + text.VirtualAddress
    hi = lo + text.Misc_VirtualSize
    code = pe.get_memory_mapped_image()[text.VirtualAddress:text.VirtualAddress + text.Misc_VirtualSize]
    start_set = set(starts)
    callers: dict[int, set[int]] = collections.defaultdict(set)
    for i in range(len(code) - 4):
        if code[i] != 0xE8:
            continue
        t = lo + i + 5 + struct.unpack_from("<i", code, i + 1)[0]
        if t in start_set:
            j = bisect.bisect_right(starts, lo + i) - 1
            if j >= 0:
                callers[t].add(starts[j])
    return callers, lo, hi


def journal_context(paths: list[Path]) -> list[tuple[int, str]]:
    """Отрезки журнала по seq: бой, ход ИИ, иначе последнее окно/фаза человека. Список (seq, метка) по возрастанию."""
    marks: list[tuple[int, str]] = []
    for p in paths:
        battle = ai = None
        top = "меню"
        in_game = False
        with p.open(encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if '"ev":"battle_' not in line and '"ev":"ai_turn"' not in line and '"ev":"ui_open"' not in line \
                        and '"ev":"game_phase"' not in line and '"ev":"site_visit"' not in line:
                    continue
                try:
                    e = json.loads(line)
                except json.JSONDecodeError:
                    continue
                ev, seq = e.get("ev"), e.get("seq", 0)
                if ev == "battle_start":
                    battle = "бой (ИИ)" if ai else "бой (человек)"
                elif ev == "battle_end":
                    battle = None
                elif ev == "ai_turn":
                    ai = e.get("phase") == "begin" and e.get("cause") == "ai"
                elif ev == "ui_open":
                    top = e.get("name") or top
                elif ev == "game_phase":
                    in_game = e.get("what") in ("new", "load")
                    top = f"загрузка ({e.get('what')})" if in_game else "меню"
                elif ev == "site_visit":
                    top = "место на карте"
                label = battle or ("ход ИИ" if ai else f"человек: {top}" if in_game else "запуск и меню")
                if not marks or marks[-1][1] != label:
                    marks.append((seq, label))
    marks.sort()
    return marks


def context_of(marks: list[tuple[int, str]], seq: int) -> str:
    if not marks or seq <= 0:
        return "?"
    i = bisect.bisect_right([m[0] for m in marks], seq) - 1
    return marks[i][1] if i >= 0 else "запуск"


def classify(rows, hits, ref_test, ref_port, ref_doc, names) -> dict[int, str]:
    level = {}
    for f, _area, cls, safe in rows:
        if not safe:
            continue
        if f in ref_test:
            level[f] = "harness"
        elif f in ref_port:
            level[f] = "port"
        elif f in ref_doc:
            level[f] = "docs"
        elif cls and ENUM.match(cls):
            level[f] = "enum"
        elif cls and not cls.startswith("?") and cls in names:
            level[f] = "class"
        else:
            level[f] = "none"
    return level


def area_table(rows, hits, level, marks) -> list[str]:
    """Строка на область: исполнено и как сверено; «нигде» исполненных — по событию журнала, где функция впервые исполнилась
    (бой — сверяет С1/С2 по журналу партии, ход ИИ — харнессы ходов ИИ; это сверка поведения, а не адреса)."""
    lines = ["| Область | Начал | Исполнено | Исп.: адрес в харнессе / в порту | Исп.: адрес в доках | Исп.: класс в порту |"
             " Исп.: перечисление | Исп.: нигде (в бою / в ходе ИИ / прочее) | Не исп.: адрес в порту | Не исп.: класс в порту |"
             " Не исп.: нигде |", "|---" * 11 + "|"]
    tot = collections.Counter()
    by_area = collections.defaultdict(collections.Counter)
    for f, area, _cls, safe in rows:
        if not safe:
            continue
        c = by_area[area]
        lv = level[f]
        c["n"] += 1
        if f in hits:
            c["ex"] += 1
            c["ex_" + lv] += 1
            if lv == "none":
                where = marks.get(f, "?")
                c["none_" + ("battle" if where.startswith("бой") else "ai" if where == "ход ИИ" else "other")] += 1
        elif lv in ("harness", "port"):
            c["no_ref"] += 1
        elif lv == "class":
            c["no_class"] += 1
        else:
            c["no_none"] += 1  # и «доки», «перечисление»: порт по адресу не написан
    for k in list(AREAS) + ["shared", "unowned"]:
        c = by_area.get(k, collections.Counter())
        tot.update(c)
        lines.append(row(AREAS.get(k, k), c))
    lines.append(row("**Всего**", tot))
    return lines


def row(name: str, c: collections.Counter) -> str:
    n = max(1, c["n"])
    ex = c["ex"]
    return (f"| {name} | {c['n']} | {ex} ({100 * ex / n:.0f} %) | {c['ex_harness']} / {c['ex_port']} | {c['ex_docs']} | "
            f"{c['ex_class']} | {c['ex_enum']} | {c['ex_none']} ({c['none_battle']} / {c['none_ai']} / {c['none_other']}) | "
            f"{c['no_ref']} | {c['no_class']} | {c['no_none']} |")


def groups(rows, hits, level, callers, marks) -> tuple[list[dict], list[dict]]:
    """Группы по классу в областях логики: неисполненные и исполненные без адреса в порту."""
    cls_of = {f: cls for f, _a, cls, _s in rows}
    area_of = {f: a for f, a, _c, _s in rows}
    miss = collections.defaultdict(list)
    unref = collections.defaultdict(list)
    for f, area, cls, safe in rows:
        if not safe or area not in LOGIC:
            continue
        key = (area, cls or "(без класса)")
        if f not in hits:
            miss[key].append(f)
        elif level[f] in ("docs", "class", "enum", "none"):
            unref[key].append(f)

    def describe(key, fs, executed):
        area, cls = key
        outside_exec = collections.Counter()
        no_callers = 0
        for f in fs:
            cs = callers.get(f, set())
            if not cs:
                no_callers += 1
            for c in cs:
                if cls_of.get(c) != cls and c in hits:
                    outside_exec[c] += 1
        ctx = collections.Counter(marks.get(f, "?") for f in fs if f in hits) if executed else collections.Counter()
        refs = collections.Counter(level[f] for f in fs)
        return {"area": area, "class": cls, "display": bool(DISPLAY.search(cls)), "count": len(fs),
                "funcs": [f"0x{f:x}" for f in sorted(fs)], "virtual_only": no_callers,
                "exec_callers": [(f"0x{c:x}", cls_of.get(c) or area_of.get(c, "")) for c, _n in outside_exec.most_common(6)],
                "contexts": ctx.most_common(4), "levels": dict(refs)}

    m = [describe(k, v, False) for k, v in miss.items()]
    u = [describe(k, v, True) for k, v in unref.items()]
    order = lambda g: (g["display"], -g["count"], g["class"])  # noqa: E731
    return sorted(m, key=order), sorted(u, key=order)


def group_lines(gs: list[dict], executed: bool, limit: int) -> list[str]:
    head = ("| Область | Класс | Функций | Из них только виртуально | Как сверено | Где исполнилось впервые | Адреса |" if executed
            else "| Область | Класс | Функций | Из них только виртуально | Адрес в порту/доках | Исполненные вызывающие | Адреса |")
    lines = [head, "|---" * 7 + "|"]
    for g in [g for g in gs if g["count"] >= BIG_GROUP][:limit]:
        lv = ", ".join(f"{LEVEL_RU[k]} {v}" for k, v in sorted(g["levels"].items(), key=lambda kv: LEVELS.index(kv[0])))
        addrs = ", ".join(f"`{a}`" for a in g["funcs"][:8]) + (" …" if g["count"] > 8 else "")
        if executed:
            mid = "; ".join(f"{k} {v}" for k, v in g["contexts"]) or "—"
        else:
            mid = ", ".join(f"`{a}` {c.split('@?%')[0]}".strip() for a, c in g["exec_callers"]) or "—"
        cls = g["class"].split("@?%")[0]   # имя RTTI с путём исходника (`CResultSender@?%D:\Source\…`) — без пути
        lines.append(f"| {AREAS.get(g['area'], g['area'])} | `{cls}` | {g['count']} | {g['virtual_only']} | {lv} | {mid} |"
                     f" {addrs} |")
    return lines


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("cov", nargs="+", help="файлы .cov.txt или каталоги с ними")
    ap.add_argument("--exe", type=Path, required=True, help="Discipl2.exe (game-pristine) — для графа вызовов")
    ap.add_argument("--functions", type=Path, default=FUNCTIONS)
    ap.add_argument("--out", type=Path, default=ROOT / ".cache" / "probe-coverage" / "map.md")
    ap.add_argument("--json", type=Path)
    ap.add_argument("--limit", type=int, default=40)
    a = ap.parse_args(argv)
    sys.stdout.reconfigure(encoding="utf-8")

    if not a.exe.is_file():
        print(f"нет {a.exe}", file=sys.stderr)
        return 1
    rows = load_all_functions(a.functions)
    starts = [f for f, *_ in rows]
    files = cov_files(a.cov)
    if not files:
        print("нет файлов .cov.txt", file=sys.stderr)
        return 1
    hits: dict[int, int] = {}
    marks: dict[int, str] = {}   # функция → событие журнала её первого исполнения (по журналу той же партии)
    for p in files:
        journal = p.with_name(p.name.removesuffix(".cov.txt") + ".jsonl")
        context = journal_context([journal]) if journal.exists() else []
        for f, seq in read_hits(p).items():
            hits.setdefault(f, seq)
            marks.setdefault(f, context_of(context, seq))
    armed = {f for f, _a, _c, s in rows if s}
    if not armed:
        print(f"в {a.functions} нет надёжных начал (safe = 1)", file=sys.stderr)
        return 1
    stray = set(hits) - armed
    if stray:
        print(f"исполненные адреса вне надёжных начал: {len(stray)} (" + ", ".join(f"0x{f:x}" for f in sorted(stray)[:10]) + ")",
              file=sys.stderr)
    callers, lo, hi = call_graph(a.exe, starts)
    ref_test, n_test = referenced(source_files(TEST_DIRS, (".cs", ".py")), starts, lo, hi)
    ref_port, n_port = referenced(source_files(PORT_DIRS, (".cs",)), starts, lo, hi)
    ref_doc, _ = referenced(source_files(DOC_DIRS, (".md",)), starts, lo, hi)
    level = classify(rows, hits, ref_test, ref_port, ref_doc, n_test | n_port)
    miss, unref = groups(rows, hits, level, callers, marks)

    ex = len(armed & set(hits))
    lines = [f"Файлов покрытия {len(files)}, надёжных начал {len(armed)}, исполнено {ex} ({100 * ex / len(armed):.1f} %); "
             f"адресов в тестах — функций {len(ref_test & armed)}, в коде порта {len(ref_port & armed)}, в доках {len(ref_doc & armed)}.",
             "", *area_table(rows, hits, level, marks), "", "### Неисполненные группы логики (класс ≥ 5 функций)", "",
             *group_lines(miss, False, a.limit), "", "### Исполненные, но без адреса в порту и тестах (класс ≥ 5 функций)", "",
             *group_lines(unref, True, a.limit), ""]
    a.out.parent.mkdir(parents=True, exist_ok=True)
    a.out.write_text("\n".join(lines) + "\n", encoding="utf-8")
    if a.json:
        a.json.parent.mkdir(parents=True, exist_ok=True)
        a.json.write_text(json.dumps({"missed": miss, "unreferenced": unref}, ensure_ascii=False, indent=1), encoding="utf-8")
    print("\n".join(lines[:len(AREAS) + 6]))
    print(f"карта -> {a.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
