#!/usr/bin/env python3
"""stack_sample.py — грубый сэмплирующий профиль .NET без dotnet-trace: снимки стеков `dotnet-stack report` по кругу.

Запускает команду бенчмарка в режиме `--loop=<случай>` (она печатает `pid=N`), пока та крутит случай, снимает стеки
всех потоков раз в ~0,8 с и считает по потокам, где есть метка (`--marker`, часть имени метода):
  * self      — сколько снимков метод был верхним кадром (где стоит CPU);
  * inclusive — в скольких снимках метод был где-то на стеке.
Сэмплов мало (десятки), поэтому это ранжирование «где горячо», а не точные проценты. Godot-процесс тоже годится: рантайм .NET
в нём слушает диагностику так же.

Пример:
  py -3 tools/bench/stack_sample.py --marker GameSession.EndTurn --seconds 60 -- \
      dotnet tools/bench/D2.Bench/bin/Release/net10.0/D2.Bench.dll --loop=ai_endturn_t48 --seconds=70
"""
from __future__ import annotations

import argparse
import collections
import re
import subprocess
import sys
import threading
import time

FRAME = re.compile(r"^\s{2}(?:(?P<module>[^!\s]+)!)?(?P<name>[^(]+)")


GENERIC = re.compile(r"`\d+[\[<][^\[\]<>]*[\]>]")


def short(frame: str) -> str | None:
    # Обобщённые аргументы (`HashSet`1<value class System.ValueTuple`2<int32,int32>>`) снимаются изнутри наружу, иначе точки внутри
    # них рвут имя: остаётся «HashSet.FindItemIndex».
    text = frame.split("(", 1)[0]
    while (stripped := GENERIC.sub("", text)) != text:
        text = stripped
    m = FRAME.match(text)
    if not m:
        return None
    parts = m.group("name").strip().split(".")
    # Namespace.Class.Method → Class.Method (вложенные классы и лямбды оставляем как есть).
    return ".".join(parts[-2:]) if len(parts) >= 2 else parts[0]


def threads(report: str) -> list[list[str]]:
    result, current = [], None
    for line in report.splitlines():
        if line.startswith("Thread ("):
            current = []
            result.append(current)
        elif current is not None and line.startswith("  ") and line.strip() != "CPU_TIME" and line.strip():
            if (name := short(line)) is not None:
                current.append(name)
    return result


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--marker", required=True, help="часть имени кадра, по которой выбираются потоки")
    ap.add_argument("--seconds", type=float, default=40)
    ap.add_argument("--top", type=int, default=20)
    ap.add_argument("--warmup", type=float, default=5, help="секунд до первого снимка (прогрев JIT, загрузка)")
    ap.add_argument("cmd", nargs=argparse.REMAINDER)
    a = ap.parse_args()
    cmd = a.cmd[1:] if a.cmd and a.cmd[0] == "--" else a.cmd
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
    pid: list[int] = []

    def pump() -> None:
        for line in proc.stdout:  # type: ignore[union-attr]
            if not pid and (m := re.search(r"pid=(\d+)", line)):
                pid.append(int(m.group(1)))

    threading.Thread(target=pump, daemon=True).start()
    deadline = time.time() + 120
    while not pid and time.time() < deadline and proc.poll() is None:
        time.sleep(0.2)
    if not pid:
        print("нет pid= в выводе команды", file=sys.stderr)
        proc.kill()
        return 2
    time.sleep(a.warmup)
    self_count: collections.Counter[str] = collections.Counter()
    incl_count: collections.Counter[str] = collections.Counter()
    samples = 0
    until = time.time() + a.seconds
    while time.time() < until and proc.poll() is None:
        out = subprocess.run(["dotnet-stack", "report", "-p", str(pid[0])], capture_output=True, text=True,
                             encoding="utf-8", errors="replace").stdout
        for stack in threads(out):
            if stack and any(a.marker in f for f in stack):
                samples += 1
                self_count[stack[0]] += 1
                for f in set(stack):
                    incl_count[f] += 1
    # Команда сама выходит по своему --seconds: kill() обёртки (godot.cmd) не снял бы дочерний процесс Godot, поэтому — ждать.
    try:
        proc.wait(timeout=600)
    except subprocess.TimeoutExpired:
        print(f"команда не вышла сама — проверьте процесс pid={pid[0]}", file=sys.stderr)
        proc.kill()
    print(f"samples={samples} marker={a.marker}")
    print("\nself (верхний кадр):")
    for name, n in self_count.most_common(a.top):
        print(f"  {n * 100 / max(samples, 1):5.1f}%  {name}")
    print("\ninclusive (на стеке):")
    for name, n in incl_count.most_common(a.top + 10):
        print(f"  {n * 100 / max(samples, 1):5.1f}%  {name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
