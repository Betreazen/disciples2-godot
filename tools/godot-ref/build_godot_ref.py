#!/usr/bin/env python3
"""Сборка компактного справочника по API Godot из самого движка.

Источник — `godot --headless --dump-extension-api-with-docs`: единственный дамп,
в котором есть описания (у `--doctool` на релизной сборке все <description> пустые).

Итог — `godot47.jsonl`: по строке на класс, описания обрезаны до первых
предложений, BBCode снят. Полный дамп ~11.8 МБ, справочник ~2 МБ.

    python tools/godot-ref/build_godot_ref.py            # запустит godot сам
    python tools/godot-ref/build_godot_ref.py --api extension_api.json
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

OUT = Path(__file__).with_name("godot47.jsonl")
GODOT = os.environ.get("GODOT_BIN", r"C:\Tools\bin\godot.cmd")
DESC_MAX = 240

_URL = re.compile(r"\[url=[^\]]*\](.*?)\[/url\]", re.S)
_TAG_ARG = re.compile(r"\[(?:member|method|constant|signal|param|enum|theme_item|annotation)\s+([^\]]+)\]")
_TAG_CLS = re.compile(r"\[([A-Z][A-Za-z0-9_]*)\]")
_TAG_ANY = re.compile(r"\[/?[a-z][^\]]*\]")


def clean(text: str | None) -> str:
    """Снять BBCode и обрезать до первых предложений (<= DESC_MAX символов)."""
    if not text:
        return ""
    t = _URL.sub(r"\1", text)
    t = _TAG_ARG.sub(lambda m: m.group(1).split(".")[-1], t)
    t = _TAG_CLS.sub(r"\1", t)
    t = _TAG_ANY.sub("", t)
    t = re.sub(r"\s+", " ", t).strip()
    if len(t) <= DESC_MAX:
        return t
    cut = t[:DESC_MAX]
    dot = max(cut.rfind(". "), cut.rfind("! "), cut.rfind("? "))
    return (cut[: dot + 1] if dot > 60 else cut.rstrip() + "...").strip()


def type_of(t: str | None) -> str:
    """`enum::Node.InternalMode` -> `Node.InternalMode`, `typedarray::24/17:Node` -> `Node[]`."""
    if not t:
        return "void"
    if t.startswith("typedarray::"):
        return type_of(t.split(":")[-1]) + "[]"
    for p in ("enum::", "bitfield::"):
        if t.startswith(p):
            return t[len(p):]
    return t


def args_of(m: dict) -> str:
    parts = []
    for a in m.get("arguments", []):
        s = f"{type_of(a['type'])} {a['name']}"
        if "default_value" in a:
            s += f"={a['default_value']}"
        parts.append(s)
    if m.get("is_vararg"):
        parts.append("...")
    return ", ".join(parts)


def quals(m: dict) -> str:
    q = [k for k in ("static", "const", "virtual") if m.get(f"is_{k}")]
    return " ".join(q)


def method(m: dict) -> dict:
    r = {"n": m["name"], "r": type_of((m.get("return_value") or m.get("return_type") and {"type": m["return_type"]} or {}).get("type")),
         "a": args_of(m)}
    if quals(m):
        r["q"] = quals(m)
    if clean(m.get("description")):
        r["d"] = clean(m["description"])
    return r


def klass(c: dict, kind: str) -> dict:
    out = {"n": c["name"], "k": kind}
    if c.get("inherits"):
        out["i"] = c["inherits"]
    if clean(c.get("brief_description")):
        out["b"] = clean(c["brief_description"])
    if clean(c.get("description")):
        out["d"] = clean(c["description"])
    props = []
    for p in c.get("properties", []) + c.get("members", []):
        e = {"n": p["name"], "t": type_of(p["type"])}
        for src, dst in (("getter", "g"), ("setter", "s")):
            if p.get(src):
                e[dst] = p[src]
        if clean(p.get("description")):
            e["d"] = clean(p["description"])
        props.append(e)
    if props:
        out["p"] = props
    methods = [method(m) for m in c.get("methods", [])]
    if methods:
        out["m"] = methods
    sigs = []
    for s in c.get("signals", []):
        e = {"n": s["name"], "a": args_of(s)}
        if clean(s.get("description")):
            e["d"] = clean(s["description"])
        sigs.append(e)
    if sigs:
        out["g"] = sigs
    enums = []
    for en in c.get("enums", []):
        enums.append({"n": en["name"],
                      "v": [[v["name"], v["value"], clean(v.get("description"))] for v in en["values"]]})
    if enums:
        out["e"] = enums
    consts = [[x["name"], x.get("value"), clean(x.get("description"))] for x in c.get("constants", [])]
    if consts:
        out["c"] = consts
    return out


def dump_api(path: Path) -> Path:
    """Запустить движок и получить extension_api.json."""
    subprocess.run([GODOT, "--headless", "--dump-extension-api-with-docs"],
                   cwd=path, check=True, stdout=subprocess.DEVNULL)
    return path / "extension_api.json"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--api", help="готовый extension_api.json (иначе движок запускается сам)")
    ap.add_argument("--out", default=str(OUT))
    a = ap.parse_args()

    with tempfile.TemporaryDirectory() as tmp:
        api = Path(a.api) if a.api else dump_api(Path(tmp))
        data = json.loads(api.read_text(encoding="utf-8"))

    singles = {s["name"]: s["type"] for s in data.get("singletons", [])}
    rows = [{"n": "@meta", "k": "meta", "b": data["header"]["version_full_name"]}]
    for c in data["classes"]:
        r = klass(c, "class")
        if c["name"] in singles:
            r["k"] = "singleton"
        rows.append(r)
    for c in data.get("builtin_classes", []):
        rows.append(klass(c, "builtin"))
    for en in data.get("global_enums", []):
        rows.append({"n": en["name"], "k": "enum",
                     "v": [[v["name"], v["value"], clean(v.get("description"))] for v in en["values"]]})
    utils = [method(u) for u in data.get("utility_functions", [])]
    if utils:
        rows.append({"n": "@global", "k": "utility", "b": "Глобальные функции (@GlobalScope)", "m": utils})

    out = Path(a.out)
    with out.open("w", encoding="utf-8", newline="\n") as f:
        for r in rows:
            f.write(json.dumps(r, ensure_ascii=False, separators=(",", ":")) + "\n")
    print(f"{out}: {len(rows)} записей, {out.stat().st_size/1048576:.2f} МБ, {data['header']['version_full_name']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
