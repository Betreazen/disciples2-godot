#!/usr/bin/env python3
r"""Покрытие кода `Discipl2.exe` зондом оригинала (схема 7.3, `tools/d2probe/mss32/src/probecov.cpp`).

Две команды:

    py tools/probe_coverage.py make-list --exe ../disciples-modding/game-pristine/Discipl2.exe \
        --procs <выгрузка REA list_procedures> --rea-dir ../disciples-godot/.analysis/rea
        # -> tools/d2probe/coverage/functions.tsv: начала функций с областью и классом (tools/port_status_count.py)

    py tools/probe_coverage.py report <журнал.cov.txt или каталог> [...] [--out .cache/probe-coverage/report.md]
        # свод нескольких партий: исполнено по областям, неисполненные функции игровой логики по классам

Список для зонда (`probe/coverage.txt`) из `functions.tsv` кладёт `tools/d2probe/install.py --coverage`: только «надёжные»
начала (`safe` = 1) — цель прямого вызова, запись vtable или адрес, лежащий в образе константой (указатель на функцию).
Прочие процедуры Ghidra (найденные эвристикой, ни разу не адресованные) не трогаются: 0xCC в ложное начало посреди данных
секции кода (таблицы переходов `switch`) испортил бы данные. Такие функции идут в отчёт как «не размечено».
"""
from __future__ import annotations

import argparse
import collections
import hashlib
import re
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FUNCTIONS = ROOT / "tools" / "d2probe" / "coverage" / "functions.tsv"

# Области игровой логики: их неисполненные функции — список для синтетических карт («закрыть всю логику»).
LOGIC = ("battle", "world_visitors", "world_effects", "world_msgs", "world_objects", "events", "magic", "ai", "hotseat")

# Классы показа внутри областей логики (экран боя `CBattleViewerInterf`, анимации, окна): их функции — не правила, отчёт кладёт
# их отдельным списком после классов логики.
DISPLAY = re.compile(r"Interf|Viewer|Anim|Display|Image|Sound|Cursor|Popup|Dialog|Button|Encyclop|Widget|Picture|ListBox")


def not_instruction_starts(exe: Path, candidates) -> set[int]:
    """Адреса, которые не начинают инструкцию: линейный разбор от ближайшей цели прямого `call`/`jmp rel32` (или начала
    `.text`) не попадает в них точно. 19.10 четыре таких «начала» (случайный dword в `.rdata` совпал с ложной процедурой
    Ghidra) получили 0xCC в байт операнда, и ход ИИ уронил игру на `0x58AE84` (`push [ebp+8]` → `push [ebp-0x34]` у
    `0x46464b`). Разбор, сбившийся на данных (таблица `switch`), тоже отвергает адрес: начало без точки лучше порчи кода."""
    import bisect

    import capstone
    import pefile

    pe = pefile.PE(str(exe), fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    img = pe.get_memory_mapped_image()
    text = next(s for s in pe.sections if s.Name.rstrip(b"\0") == b".text")
    lo = base + text.VirtualAddress
    hi = lo + text.Misc_VirtualSize
    code = img[text.VirtualAddress:text.VirtualAddress + text.Misc_VirtualSize]
    anchors = {lo}
    for i, b in enumerate(code[:-4]):
        if b in (0xE8, 0xE9):
            t = lo + i + 5 + struct.unpack_from("<i", code, i + 1)[0]
            if lo <= t < hi:
                anchors.add(t)
    anchors = sorted(anchors)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    bad = set()
    for c in candidates:
        if not lo <= c < hi:
            bad.add(c)
            continue
        a = anchors[bisect.bisect_right(anchors, c) - 1]
        if a == c:
            continue
        pos = a
        for ins in md.disasm(code[a - lo:c - lo + 16], a):
            if ins.address >= c:
                break
            pos = ins.address + ins.size
        if pos != c:
            bad.add(c)
    return bad


def check_list(exe: Path, functions: Path) -> int:
    """Снимает `safe` у строк `functions.tsv`, которые не начинают инструкцию (`not_instruction_starts`)."""
    lines = functions.read_text(encoding="utf-8").splitlines()
    safe = [int(l.split("\t")[0], 16) for l in lines if l and not l.startswith("#") and l.rstrip().endswith("\t1")]
    bad = not_instruction_starts(exe, safe)
    out = []
    for l in lines:
        if l and not l.startswith("#") and int(l.split("\t")[0], 16) in bad:
            l = l.rstrip()[:-1] + "0"
        out.append(l)
    functions.write_text("\n".join(out) + "\n", encoding="utf-8", newline="\n")
    print(f"надёжных начал {len(safe)}, не начало инструкции {len(bad)}: "
          + ", ".join(f"0x{f:x}" for f in sorted(bad)))
    return 0


def make_list(exe: Path, procs: Path, rea: Path, out: Path) -> int:
    sys.path.insert(0, str(ROOT / "tools"))
    import pefile
    from port_status_count import AREAS, label_functions

    ctx = label_functions(exe, procs, rea)
    funcs = ctx["funcs"]
    text = ctx["text"]
    pe = pefile.PE(str(exe), fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    img = pe.get_memory_mapped_image()

    called = set(ctx["callers"])
    in_vtable = set(ctx["vtable"])
    pointed = set()
    for i in range(len(img) - 3):
        v = struct.unpack_from("<I", img, i)[0]
        if text[0] <= v < text[1] and v in funcs:
            pointed.add(v)
    rows = []
    for f in sorted(funcs):
        if not (text[0] <= f < text[1]):
            continue
        first = img[f - base]
        safe = (f in called or f in in_vtable or f in pointed or f == ctx["entry"]) and first != 0xCC
        area = ctx["area"].get(f, "unowned")
        cls = ctx["class"].get(f, "")
        cls = cls if cls not in ("shared", None) else ""
        rows.append((f, area, cls, safe))
    misplaced = not_instruction_starts(exe, [f for f, _a, _c, s in rows if s])
    rows = [(f, area, cls, safe and f not in misplaced) for f, area, cls, safe in rows]
    data = exe.read_bytes()
    out.parent.mkdir(parents=True, exist_ok=True)
    with out.open("w", encoding="utf-8", newline="\n") as fh:
        fh.write(f"# exe_size {len(data)}\n# exe_sha256 {hashlib.sha256(data).hexdigest()}\n")
        fh.write("# address\tarea\tclass\tsafe (1 - прямой вызов, vtable или указатель; 0 - только эвристика Ghidra)\n")
        for f, area, cls, safe in rows:
            fh.write(f"0x{f:x}\t{area}\t{cls}\t{int(safe)}\n")
    by_area = collections.Counter(a for _f, a, _c, s in rows if s)
    print(f"функций {len(rows)}, надёжных начал {sum(1 for r in rows if r[3])} -> {out}")
    for k in list(AREAS) + ["shared", "unowned"]:
        print(f"  {AREAS.get(k, k)}: {by_area.get(k, 0)}")
    return 0


def load_functions(path: Path) -> tuple[dict[int, tuple[str, str, bool]], int | None]:
    funcs: dict[int, tuple[str, str, bool]] = {}
    exe_size = None
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("# exe_size "):
            exe_size = int(line.split()[2])
        if not line or line.startswith("#"):
            continue
        addr, area, cls, safe = (line.split("\t") + ["", "", "", "0"])[:4]
        funcs[int(addr, 16)] = (area, cls, safe == "1")
    return funcs, exe_size


def read_cov(path: Path) -> tuple[set[int], int | None]:
    """Исполненные адреса файла `.cov.txt` и число поставленных точек (из заголовка)."""
    hit: set[int] = set()
    armed = None
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if line.startswith("# coverage "):
            parts = line.split()
            if "armed" in parts:
                armed = int(parts[parts.index("armed") + 1])
            continue
        if not line or line.startswith("#"):
            continue
        hit.add(int(line.split()[0], 16))
    return hit, armed


def cov_files(args: list[str]) -> list[Path]:
    out: list[Path] = []
    for a in args:
        p = Path(a)
        out.extend(sorted(p.rglob("*.cov.txt")) if p.is_dir() else [p])
    return out


def report(paths: list[Path], functions: Path, out: Path | None) -> int:
    sys.path.insert(0, str(ROOT / "tools"))
    from port_status_count import AREAS

    funcs, _ = load_functions(functions)
    armed = {f for f, (_a, _c, s) in funcs.items() if s}
    hit: set[int] = set()
    per_file = []
    for p in paths:
        h, n = read_cov(p)
        hit |= h
        per_file.append((p, len(h), n))
    unknown = hit - set(funcs)
    lines = ["# Покрытие `Discipl2.exe` зондом", "",
             f"Файлов покрытия: {len(paths)}; поставлено точек (надёжных начал): {len(armed)}; исполнено: {len(hit & armed)}"
             f" ({100 * len(hit & armed) / max(1, len(armed)):.1f} %); адресов вне описи: {len(unknown)}.", ""]
    for p, n, a in per_file:
        lines.append(f"- `{p.name}`: исполнено {n} из {a if a is not None else '?'}")
    lines += ["", "| Область | Начал | Исполнено | % | Не размечено (эвристика Ghidra) |", "|---|---|---|---|---|"]
    by_area: dict[str, list[int]] = collections.defaultdict(list)
    heuristic = collections.Counter()
    for f, (area, _cls, safe) in funcs.items():
        if safe:
            by_area[area].append(f)
        else:
            heuristic[area] += 1
    for k in list(AREAS) + ["shared", "unowned"]:
        fs = by_area.get(k, [])
        n = sum(1 for f in fs if f in hit)
        lines.append(f"| {AREAS.get(k, k)} | {len(fs)} | {n} | {100 * n / max(1, len(fs)):.1f} | {heuristic.get(k, 0)} |")
    lines += ["", "## Неисполненные функции игровой логики", "",
              "По областям и классам-хозяевам (`tools/port_status_count.py`, шаги 1–3); без класса — помощник области.", ""]
    for k in LOGIC:
        miss = [f for f in by_area.get(k, []) if f not in hit]
        if not miss:
            continue
        by_cls = collections.defaultdict(list)
        for f in miss:
            by_cls[funcs[f][1] or "(без класса)"].append(f)
        lines += [f"### {AREAS.get(k, k)} — {len(miss)} из {len(by_area[k])}", ""]
        ordered = sorted(by_cls.items(), key=lambda kv: (bool(DISPLAY.search(kv[0])), -len(kv[1]), kv[0]))
        shown_display = False
        for cls, fs in ordered:
            if DISPLAY.search(cls) and not shown_display:
                lines += ["", "Показ (окна, экран боя, анимации) в этой области:", ""]
                shown_display = True
            lines.append(f"- `{cls}` ({len(fs)}): " + ", ".join(f"`0x{f:x}`" for f in sorted(fs)))
        lines.append("")
    text = "\n".join(lines) + "\n"
    if out:
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(text, encoding="utf-8")
        print(f"отчёт -> {out}")
    print("\n".join(lines[:8 + len(per_file) + 4 + len(AREAS) + 2]))
    return 0


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    m = sub.add_parser("make-list", help="опись начал функций для зонда")
    m.add_argument("--exe", type=Path, required=True)
    m.add_argument("--procs", type=Path, required=True)
    m.add_argument("--rea-dir", type=Path, required=True)
    m.add_argument("--out", type=Path, default=FUNCTIONS)
    c = sub.add_parser("check-list", help="снять safe у начал, которые не начинают инструкцию")
    c.add_argument("--exe", type=Path, required=True)
    c.add_argument("--functions", type=Path, default=FUNCTIONS)
    r = sub.add_parser("report", help="свод покрытия по файлам .cov.txt")
    r.add_argument("cov", nargs="+", help="файлы <журнал>.cov.txt или каталоги с ними")
    r.add_argument("--functions", type=Path, default=FUNCTIONS)
    r.add_argument("--out", type=Path, default=ROOT / ".cache" / "probe-coverage" / "report.md")
    a = ap.parse_args(argv)
    sys.stdout.reconfigure(encoding="utf-8")
    if a.cmd == "make-list":
        return make_list(a.exe, a.procs, a.rea_dir, a.out)
    if a.cmd == "check-list":
        return check_list(a.exe, a.functions)
    files = cov_files(a.cov)
    if not files:
        print("нет файлов .cov.txt", file=sys.stderr)
        return 1
    return report(files, a.functions, a.out)


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
