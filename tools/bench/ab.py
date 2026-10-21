#!/usr/bin/env python3
"""ab.py — замер «до/после» двух рабочих копий вперемешку (этап 11 пайплайна) и вердикт попарно.

Каждый прогон — свежий процесс. Порядок в паре чередуется (ABBA: до→после, после→до, …): у коротких замеров есть эффект
«второй в паре» и двугорбый разброс между процессами (11 или 17 мс у load_scenario_ptc), из-за которых правило «все прогоны
строго по одну сторону» даёт и ложные «медленнее», и ложные «то же» (журнал docs/perf-log.md, круг 14.10). Вердикт — по парам:
«после» быстрее в k парах из n (критерий знаков, p < 0,05) и медиана отношения дальше порога; рядом — медианы и разброс.

  py -3 tools/bench/ab.py ../dg-wt-base ../dg-wt-hyp --filter=ai_endturn_t30,ai_endturn_t48 --pairs 10 --logic
  py -3 tools/bench/ab.py ../dg-wt-base . --pairs 6                  # все бенчмарки: логика и сцены (run_all.py)
  py -3 tools/bench/ab.py --report bench/ab-20261014                 # только пересчитать вердикт по сохранённым прогонам

Окружение — как у run_all.py: D2_CORPUS_DIR, D2_ASSETS_DIR, GODOT. Пока идёт замер, машину ничем не нагружать.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import shutil
import statistics
import subprocess
import sys
from datetime import datetime
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
BENCH_DLL = Path("tools/bench/D2.Bench/bin/Release/net10.0/D2.Bench.dll")


def run(cmd: list[str], cwd: Path, assets: Path | None = None) -> None:
    env = {**os.environ, "D2_PORT_PROBE": "0", **({"D2_ASSETS_DIR": str(assets)} if assets else {})}
    proc = subprocess.run(cmd, cwd=cwd, env=env, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=3600)
    if proc.returncode != 0:
        sys.exit(f"код {proc.returncode}: {' '.join(cmd)} в {cwd}\n{(proc.stdout + proc.stderr)[-2000:]}")


def once(tree: Path, out: Path, extra: list[str], logic: bool, assets: Path | None) -> None:
    if logic:
        run(["dotnet", str(BENCH_DLL), *extra, f"--save={out}"], tree, assets)
    else:
        run([sys.executable, "tools/bench/run_all.py", "ab-tmp", *extra], tree, assets)
        shutil.copyfile(tree / "bench" / "ab-tmp.json", out)


def medians(path: Path) -> dict[str, float]:
    return {k: statistics.median(v) / 1000 for k, v in json.loads(path.read_text(encoding="utf-8"))["results"].items()}


def sign_p(wins: int, n: int) -> float:
    k = max(wins, n - wins)
    return min(1.0, 2 * sum(math.comb(n, i) for i in range(k, n + 1)) / 2 ** n)


def report(out: Path, threshold: float) -> int:
    pairs = []
    for b in sorted(out.glob("before*.json"), key=lambda p: int(p.stem[6:])):
        a = out / ("after" + b.stem[6:] + ".json")
        if a.exists():
            pairs.append((medians(b), medians(a)))
    if not pairs:
        sys.exit(f"в {out} нет пар before/after")
    names = sorted(set.intersection(*(set(b) & set(a) for b, a in pairs)), key=lambda n: -statistics.median(b[n] for b, _ in pairs))
    slower = False
    print(f"пар: {len(pairs)}; порог {threshold}%; вердикт — критерий знаков (p < 0,05) и медиана отношения")
    for name in names:
        before = [b[name] for b, _ in pairs]
        after = [a[name] for _, a in pairs]
        n = len(pairs)
        wins = sum(y < x for x, y in zip(before, after, strict=True))
        ratio = statistics.median(y / x for x, y in zip(before, after, strict=True))
        p = sign_p(wins, n)
        verdict = "same"
        if p < 0.05 and abs(ratio - 1) * 100 > threshold:
            verdict = "FASTER" if wins > n / 2 else "SLOWER"
        slower |= verdict == "SLOWER"
        print(f"{name:30} {statistics.median(before):9.2f} мс [{min(before):.2f}–{max(before):.2f}] → "
              f"{statistics.median(after):9.2f} мс [{min(after):.2f}–{max(after):.2f}]  {(ratio - 1) * 100:+6.1f}%  "
              f"{wins}/{n} p={p:.3f}  {verdict}")
    return 1 if slower else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("before", nargs="?", type=Path)
    ap.add_argument("after", nargs="?", type=Path)
    ap.add_argument("--filter", default="", help="имена бенчмарков через запятую (как у run_all.py)")
    ap.add_argument("--pairs", type=int, default=6)
    ap.add_argument("--logic", action="store_true", help="только D2.Bench (Release), без сцен Godot")
    ap.add_argument("--threshold", type=float, default=5.0)
    ap.add_argument("--out", type=Path, default=None, help="каталог прогонов (по умолчанию bench/ab-<время>)")
    ap.add_argument("--report", type=Path, default=None, help="только вердикт по сохранённым прогонам")
    ap.add_argument("--assets-before", type=Path, default=None, help="D2_ASSETS_DIR стороны «до» (графика другой сборки)")
    ap.add_argument("--assets-after", type=Path, default=None, help="D2_ASSETS_DIR стороны «после»")
    a = ap.parse_args()
    if a.report:
        return report(a.report, a.threshold)
    if not (a.before and a.after):
        ap.error("нужны две рабочие копии: до и после")
    out = a.out or REPO / "bench" / f"ab-{datetime.now():%Y%m%d-%H%M%S}"
    out.mkdir(parents=True, exist_ok=True)
    extra = [f"--filter={a.filter}"] if a.filter else []
    trees = {"before": a.before.resolve(), "after": a.after.resolve()}
    for tree in trees.values():
        run(["dotnet", "build", "tools/bench/D2.Bench", "-c", "Release", "-v", "q"], tree)
        if not a.logic:
            run(["dotnet", "build", "godot/D2.Godot.csproj", "-v", "q"], tree)
    for i in range(1, a.pairs + 1):
        order = ("before", "after") if i % 2 else ("after", "before")
        for side in order:
            once(trees[side], out / f"{side}{i}.json", extra, a.logic, a.assets_before if side == "before" else a.assets_after)
        print(f"пара {i} из {a.pairs}", flush=True)
    print(f"прогоны: {out}")
    return report(out, a.threshold)


if __name__ == "__main__":
    sys.exit(main())
