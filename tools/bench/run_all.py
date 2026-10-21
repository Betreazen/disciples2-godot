#!/usr/bin/env python3
"""run_all.py — все бенчмарки порта одной командой: логика (tools/bench/D2.Bench, Release) и сцены (godot/bench, headless).

Пишет bench/<имя>-logic.json, bench/<имя>-scene.json и общий bench/<имя>.json (результаты обоих, заголовок — оба
заголовка через « | »). Формат — как у bench.gd скилла gdscript-knowledge; сравнение двух общих файлов:
  godot --headless --path godot --script res://bench/bench_compare.gd -- ../bench/before.json ../bench/after.json [порог-%]

Окружение: D2_CORPUS_DIR (корпус, только чтение), D2_ASSETS_DIR (графика), GODOT (по умолчанию C:\\Tools\\bin\\godot.cmd).
Базовая и повторная линии — на той же машине, без другой тяжёлой работы, одной и той же сборкой.

  py -3 tools/bench/run_all.py before            # bench/before.json
  py -3 tools/bench/run_all.py after --filter=ai_endturn_t48,map_open_t48
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]


def run(cmd: list[str], cwd: Path) -> None:
    print("$", " ".join(cmd), flush=True)
    proc = subprocess.run(cmd, cwd=cwd, env={**os.environ, "D2_PORT_PROBE": "0"}, timeout=3600)
    if proc.returncode != 0:
        sys.exit(f"код {proc.returncode}: {' '.join(cmd)}")


def main() -> int:
    name = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("--") else "before"
    extra = [a for a in sys.argv[1:] if a.startswith("--filter=")]
    out = REPO / "bench"
    out.mkdir(exist_ok=True)
    godot = os.environ.get("GODOT", r"C:\Tools\bin\godot.cmd")
    run(["dotnet", "build", "tools/bench/D2.Bench", "-c", "Release", "-v", "q"], REPO)
    run(["dotnet", "build", "godot/D2.Godot.csproj", "-v", "q"], REPO)
    logic, scene = out / f"{name}-logic.json", out / f"{name}-scene.json"
    run(["dotnet", str(REPO / "tools/bench/D2.Bench/bin/Release/net10.0/D2.Bench.dll"), f"--save={logic}", *extra], REPO)
    run([godot, "--headless", "--path", "godot", "res://bench/BenchScene.tscn", "--", "--no-mods", f"--save={scene}", *extra], REPO)
    parts = [json.loads(p.read_text(encoding="utf-8")) for p in (logic, scene)]
    merged = {"header": " | ".join(p["header"] for p in parts), "iterations": 1,
              "results": {k: v for p in parts for k, v in p["results"].items()}}
    (out / f"{name}.json").write_text(json.dumps(merged, indent="\t", ensure_ascii=False), encoding="utf-8")
    print(f"saved {out / f'{name}.json'}: {len(merged['results'])} бенчмарков")
    return 0


if __name__ == "__main__":
    sys.exit(main())
