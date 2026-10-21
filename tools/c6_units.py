#!/usr/bin/env python3
"""С6: сверка наших данных юнитов (Gunits+Gattacks, cp866) с независимой таблицей
Disciples2-RL (reference/Disciples2-RL/Big_map/data_dicts_compact_lines.py, DATA).
Сопоставление по русскому имени: Gunits.NAME_TXT -> l10n/russobit/Globals/Tglobal.TEXT -> поле "кто".
Печатает: сопоставлено / расхождений по каждому полю / примеры. Ничего не чинит.

    python tools/c6_units.py
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
from dbf_dump import read  # noqa: E402

MODDING = Path(__file__).resolve().parents[2] / "disciples-modding"
sys.path.insert(0, str(MODDING / "reference" / "Disciples2-RL" / "Big_map"))
from data_dicts_compact_lines import DATA as RL  # noqa: E402

G = MODDING / "game-pristine" / "Globals"
RU = MODDING / "l10n" / "russobit" / "Globals"
EXAMPLES = 5


def main() -> int:
    txt = {r["TXT_ID"].lower(): r["TEXT"] for r in read(RU / "Tglobal.dbf")["rows"]}
    att = {r["ATT_ID"].lower(): r for r in read(G / "Gattacks.dbf")["rows"]}
    rl = {}
    for r in RL:
        rl.setdefault(r["кто"], r)  # первое вхождение имени; дубли имён в RL печатаются ниже
    dup_rl = len(RL) - len(rl)
    units = read(G / "Gunits.dbf")["rows"]
    matched, seen, unmatched = 0, set(), []
    bad = {"здоровье": [], "броня": [], "урон": [], "точн": [], "инит": []}
    for u in units:
        name = txt.get(u["NAME_TXT"].lower())
        a = att.get(u["ATTACK_ID"].lower())
        if name not in rl or a is None:
            unmatched.append("%s %s" % (u["UNIT_ID"], name))
            continue
        r = rl[name]
        matched += 1
        seen.add(name)
        for field, ours, theirs in [("здоровье", u["HIT_POINT"], r["здоровье"]),
                                    ("броня", u["ARMOR"], round(r["броня"] * 100)),
                                    ("урон", a["QTY_DAM"], r["урон"]),
                                    ("точн", a["POWER"], round(r["точн"] * 100)),
                                    ("инит", a["INITIATIVE"], r["инит"])]:
            if ours != theirs:
                bad[field].append("%s (%s): наше %s, RL %s" % (name, u["UNIT_ID"], ours, theirs))
    print("сопоставлено юнитов: %d из %d в RL (уникальных имён %d, дублей %d) / %d в Gunits"
          % (matched, len(RL), len(rl), dup_rl, len(units)))
    print("имён RL без пары в Gunits: %d" % len(set(rl) - seen))
    for name in sorted(set(rl) - seen)[:EXAMPLES]:
        print("    " + name)
    print("записей Gunits без пары в RL: %d" % len(unmatched))
    for row in unmatched[:EXAMPLES]:
        print("    " + row)
    total = 0
    for field, rows in bad.items():
        total += len(rows)
        print("%-10s расхождений %d" % (field, len(rows)))
        for row in rows[:EXAMPLES]:
            print("    " + row)
    print("итого расхождений по полям: %d" % total)
    return 0


if __name__ == "__main__":
    sys.exit(main())
