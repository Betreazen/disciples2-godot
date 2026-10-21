#!/usr/bin/env python3
"""Справочник по API Godot 4.7.2 — локально, без интернета.

    godot_ref.py Sprite2D                 сводка по классу
    godot_ref.py Sprite2D.texture         одно свойство/метод/сигнал/константа
    godot_ref.py --search "viewport texture"
    godot_ref.py --methods Node --grep child
    godot_ref.py --props Image --grep format
    godot_ref.py --tree Sprite2D          цепочка наследования

Имена печатаются в виде C# (PascalCase) со снейк-оригиналом в скобках:
правило перевода — в README.md рядом.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

DB = Path(__file__).with_name("godot47.jsonl")


def load() -> dict[str, dict]:
    with DB.open(encoding="utf-8") as f:
        return {r["n"]: r for r in map(json.loads, f)}


def cs(name: str) -> str:
    """snake_case -> PascalCase, как в биндингах C# (FORMAT_RGBA8 -> FormatRgba8)."""
    return "".join(p[:1].upper() + p[1:].lower() for p in name.split("_"))


def enum_cs(enum: dict) -> list[tuple[str, object, str]]:
    """Значения перечисления в виде C#: общий префикс снимается (FORMAT_RGBA8 -> Rgba8)."""
    vals = enum["v"]
    names = [v[0] for v in vals]
    pref = ""
    if len(names) > 1:
        head = names[0].split("_")
        for i in range(1, len(head)):
            cand = "_".join(head[:i]) + "_"
            if all(n.startswith(cand) and n[len(cand):] for n in names):
                pref = cand
    return [(cs(n[len(pref):]), v, d) for n, v, d in vals]


def both(name: str) -> str:
    c = cs(name)
    return c if c == name else f"{c} ({name})"


def sig(m: dict) -> str:
    q = (m.get("q", "") + " ").lstrip()
    return f"{q}{m['r']} {both(m['n'])}({m['a']})"


def members(r: dict) -> list[tuple[str, str, dict]]:
    """Плоский список (вид, имя, запись) по классу."""
    out = []
    out += [("prop", p["n"], p) for p in r.get("p", [])]
    out += [("method", m["n"], m) for m in r.get("m", [])]
    out += [("signal", s["n"], s) for s in r.get("g", [])]
    out += [("const", c[0], {"n": c[0], "value": c[1], "d": c[2]}) for c in r.get("c", [])]
    for e in r.get("e", []):
        out.append(("enum", e["n"], e))
        out += [("const", v[0], {"n": v[0], "value": v[1], "d": v[2], "cs": f"{e['n']}.{n}"})
                for v, (n, _, _) in zip(e["v"], enum_cs(e))]
    out += [("const", v[0], {"n": v[0], "value": v[1], "d": v[2], "cs": n})
            for v, (n, _, _) in zip(r.get("v", []), enum_cs(r) if r.get("v") else [])]
    return out


def find(db: dict, name: str) -> dict | None:
    if name in db:
        return db[name]
    low = {k.lower(): k for k in db}
    return db.get(low.get(name.lower(), ""))


def show_class(r: dict, db: dict) -> None:
    chain = []
    cur = r
    while cur:
        chain.append(cur["n"])
        cur = db.get(cur.get("i", ""))
    print(f"{r['n']} : {' < '.join(chain[1:]) or '-'}   [{r['k']}]")
    if r.get("b"):
        print(r["b"])
    if r.get("d") and r.get("d") != r.get("b"):
        print(r["d"])
    # аксессоры свойств в сводке не печатаются: они видны как сами свойства
    acc = {p[k] for p in r.get("p", []) for k in ("g", "s") if k in p}
    for kind, key, fmt in (("p", "props", lambda x: f"{both(x['n'])}:{x['t']}"),
                           ("m", "methods", lambda x: both(x["n"])),
                           ("g", "signals", lambda x: both(x["n"]))):
        items = [x for x in r.get(kind, []) if not (kind == "m" and x["n"] in acc)]
        if items:
            print(f"{key} ({len(items)}): " + ", ".join(fmt(x) for x in items))
    if r.get("e"):
        print("enums: " + ", ".join(f"{e['n']}[{len(e['v'])}]" for e in r["e"]))
    if r.get("c"):
        print("consts: " + ", ".join(c[0] for c in r["c"]))
    if r.get("v"):
        print("values: " + ", ".join(f"{v[0]}={v[1]}" for v in r["v"]))
    own = sum(len(r.get(k, [])) for k in ("p", "m", "g"))
    print(f"-- {own} собственных членов; наследуемое смотреть у {r.get('i', '-')}")


def show_member(cls: dict, name: str) -> int:
    hits = [(k, n, x) for k, n, x in members(cls) if n == name or cs(n) == cs(name)]
    if not hits:
        near = [n for _, n, _ in members(cls) if name.lower() in n.lower()]
        print(f"нет {cls['n']}.{name}" + (f"; близкое: {', '.join(near[:8])}" if near else ""))
        return 1
    for kind, _, x in hits:
        if kind == "method":
            print(f"{cls['n']}: {sig(x)}")
        elif kind == "prop":
            acc = "{ " + " ".join(f"{cs(x[k])}();" for k in ("g", "s") if k in x) + " }"
            print(f"{cls['n']}.{both(x['n'])} : {x['t']}   {acc}")
        elif kind == "signal":
            print(f"{cls['n']} signal {both(x['n'])}({x.get('a', '')})")
        elif kind == "enum":
            print(f"{cls['n']}.{x['n']} = " + ", ".join(f"{n}={v}" for n, v, _ in enum_cs(x)))
        else:
            print(f"{cls['n']}.{x.get('cs', cs(x['n']))} = {x.get('value')}   ({x['n']})")
        if x.get("d"):
            print("  " + x["d"])
    return 0


def search(db: dict, query: str, limit: int) -> None:
    words = [w.lower() for w in re.split(r"\W+", query) if w]
    scored = []
    for r in db.values():
        name = r["n"].lower()
        text = (r.get("b", "") + " " + r.get("d", "")).lower()
        score = 0
        for w in words:
            if w == name:
                score += 10
            elif w in name:
                score += 4
            if w in text:
                score += 1
        if score:
            scored.append((score, len(r["n"]), r))
    scored.sort(key=lambda t: (-t[0], t[1]))
    if not scored:
        print("ничего не найдено")
    for _, _, r in scored[:limit]:
        print(f"{r['n']} ({r['k']}{', : ' + r['i'] if r.get('i') else ''}) — {r.get('b', '')[:110]}")


def list_members(cls: dict, kind: str, grep: str | None) -> None:
    rx = re.compile(grep, re.I) if grep else None
    rows = [(k, n, x) for k, n, x in members(cls) if k == kind]
    shown = 0
    for k, n, x in rows:
        if rx and not rx.search(n):
            continue
        shown += 1
        if k == "method":
            print(f"{sig(x)}")
        elif k == "prop":
            print(f"{both(x['n'])} : {x['t']}")
        elif k == "signal":
            print(f"{both(x['n'])}({x.get('a', '')})")
        else:
            print(f"{cs(x['n'])} = {x.get('value')}")
    print(f"-- {shown} из {len(rows)} в {cls['n']}")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="справочник Godot 4.7.2 (локальный)")
    ap.add_argument("name", nargs="?", help="Класс или Класс.член")
    ap.add_argument("--search", "-s", help="поиск по имени и краткому описанию")
    ap.add_argument("--methods", help="список методов класса")
    ap.add_argument("--props", help="список свойств класса")
    ap.add_argument("--signals", help="список сигналов класса")
    ap.add_argument("--tree", help="цепочка наследования")
    ap.add_argument("--grep", help="фильтр по имени для --methods/--props/--signals")
    ap.add_argument("--limit", type=int, default=15)
    a = ap.parse_args(argv)

    db = load()

    if a.search:
        search(db, a.search, a.limit)
        return 0

    for opt, kind in (("methods", "method"), ("props", "prop"), ("signals", "signal")):
        want = getattr(a, opt)
        if want:
            cls = find(db, want)
            if not cls:
                print(f"нет класса {want}")
                return 1
            list_members(cls, kind, a.grep)
            return 0

    if a.tree:
        cur = find(db, a.tree)
        if not cur:
            print(f"нет класса {a.tree}")
            return 1
        chain = []
        while cur:
            chain.append(cur["n"])
            cur = db.get(cur.get("i", ""))
        print(" < ".join(chain))
        return 0

    if not a.name:
        ap.print_help()
        return 2

    if "." in a.name:
        cname, mname = a.name.split(".", 1)
        cls = find(db, cname)
        if not cls:
            print(f"нет класса {cname}")
            return 1
        return show_member(cls, mname)

    r = find(db, a.name)
    if not r:
        print(f"нет класса {a.name}; попробуйте --search {a.name}")
        search(db, a.name, 5)
        return 1
    show_class(r, db)
    return 0


if __name__ == "__main__":
    sys.exit(main())
