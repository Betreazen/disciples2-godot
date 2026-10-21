#!/usr/bin/env python3
"""Build the golden stack-position fixture for SgFileTests.

Runs the independent inspector from ../disciples-modding/reference/Disciples2-RL
for a few known scenarios and reshapes its per-stack dump into
``{scenario_name: [{"x": int, "y": int}, ...]}``, written to
src/D2.Data.Tests/golden/sg_stacks.json. SgFileTests compares stack count and
the set of (x, y) positions parsed by the Kaitai-based SgFile against this.

Usage: python tools/sg_golden.py
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
MODDING_ROOT = REPO_ROOT.parent / "disciples-modding"
INSPECTOR = MODDING_ROOT / "reference" / "Disciples2-RL" / "Big_map" / "tools" / "inspect_sg_map.py"
GAME_DIR = Path(os.environ.get("D2_GAME_DIR", str(MODDING_ROOT / "game-pristine")))
EXPORTS = GAME_DIR / "Exports"
GOLDEN_OUT = REPO_ROOT / "src" / "D2.Data.Tests" / "golden" / "sg_stacks.json"

SCENARIOS = ["Last Stand", "Greenskins", "Flight"]


def dump_stack_positions(scenario_name: str) -> list[dict[str, int]]:
    # A handful of exported .sg files carry literal single quotes around the
    # name (e.g. "'Greenskins'.sg") -- try the plain name first, then that form.
    candidates = [EXPORTS / f"{scenario_name}.sg", EXPORTS / f"'{scenario_name}'.sg"]
    sg_path = next((p for p in candidates if p.exists()), None)
    if sg_path is None:
        raise FileNotFoundError(candidates[0])

    with tempfile.TemporaryDirectory() as tmp:
        out_path = Path(tmp) / "stacks.json"
        subprocess.run(
            [
                sys.executable,
                str(INSPECTOR),
                str(sg_path),
                "--dump-stacks",
                "--stacks-json-output",
                str(out_path),
            ],
            check=True,
            capture_output=True,
            env={**os.environ, "PYTHONIOENCODING": "utf-8"},
        )
        rows = json.loads(out_path.read_text(encoding="utf-8"))

    return [{"x": row["x"], "y": row["y"]} for row in rows]


def main() -> None:
    golden = {name: dump_stack_positions(name) for name in SCENARIOS}
    GOLDEN_OUT.parent.mkdir(parents=True, exist_ok=True)
    GOLDEN_OUT.write_text(json.dumps(golden, indent=2), encoding="utf-8")
    for name, rows in golden.items():
        print(f"{name}: {len(rows)} stacks")
    print(f"wrote {GOLDEN_OUT}")


if __name__ == "__main__":
    main()
