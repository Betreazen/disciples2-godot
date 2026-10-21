"""Карта покрытия (`tools/coverage_map.py`): адрес внутри функции относится к ней, уровни сверки и событие первого исполнения."""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import coverage_map as cm  # noqa: E402

# Адреса вне .text Discipl2.exe (там кончается на 0x6c…): тест не должен попадать в карту как «харнесс» реальной функции.
STARTS = [0x7e1000, 0x7e1100, 0x7e1200]


def test_address_inside_function_counts_for_its_start(tmp_path):
    src = tmp_path / "a.cs"
    src.write_text("// 0x7e1150 — вызов внутри; 0x7e3000 — за концом отрезка; 0x7e0fff — до первой функции\n", encoding="utf-8")
    funcs, names = cm.referenced([src], STARTS, 0x7e1000, 0x7e2000)
    assert funcs == {0x7e1100}
    assert "Section" not in names and "вызов" not in names  # имена — только латиницей с заглавной


def test_levels_prefer_harness_then_port_then_docs_enum_class():
    a, b, c, d = 0x7e1000, 0x7e1100, 0x7e1200, 0x7e1300
    rows = [(a, "ai", "CAiTactic", True), (b, "ai", "LRaceCategory", True), (c, "ai", "CAiTactic", True), (d, "ai", "", False)]
    level = cm.classify(rows, {}, ref_test={a}, ref_port={a, b}, ref_doc=set(), names={"CAiTactic"})
    assert level == {a: "harness", b: "port", c: "class"}
    level = cm.classify(rows, {}, ref_test=set(), ref_port=set(), ref_doc=set(), names=set())
    assert level[b] == "enum" and level[c] == "none" and d not in level


def test_journal_context_labels_battle_ai_turn_and_menu(tmp_path):
    lines = [
        {"seq": 1, "ev": "ui_open", "name": "DLG_MAIN_MENU"},
        {"seq": 10, "ev": "game_phase", "what": "new"},
        {"seq": 20, "ev": "ai_turn", "phase": "begin", "cause": "ai"},
        {"seq": 30, "ev": "battle_start"},
        {"seq": 40, "ev": "battle_end"},
        {"seq": 50, "ev": "ai_turn", "phase": "end", "cause": "ai"},
        {"seq": 60, "ev": "ui_open", "name": "DLG_CAST_SPELL"},
    ]
    journal = tmp_path / "passive-001.jsonl"   # журнал зонда — без пробелов после «:»
    journal.write_text("\n".join(json.dumps(x, separators=(",", ":")) for x in lines) + "\n", encoding="utf-8")
    marks = cm.journal_context([journal])
    assert cm.context_of(marks, 5) == "запуск и меню"
    assert cm.context_of(marks, 25) == "ход ИИ"
    assert cm.context_of(marks, 35) == "бой (ИИ)"
    assert cm.context_of(marks, 65) == "человек: DLG_CAST_SPELL"
    assert cm.context_of(marks, 0) == "?"
