"""Защиты служебных скриптов: манифест корпуса не затирается через --dry-run,
журнал убитой игры читается с обрезанным символом, пустые/не-словарные строки журнала порта."""
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import corpus_run as cr  # noqa: E402
import port_log_report as plr  # noqa: E402


def test_dry_run_does_not_overwrite_existing_manifest(tmp_path, monkeypatch):
    out = tmp_path / "corpus"
    out.mkdir()
    (out / "manifest.json").write_text("[]", encoding="utf-8")
    (out / "battle-001.jsonl").write_text("keep", encoding="utf-8")
    src = tmp_path / "sample.jsonl"
    src.write_text('{"ev":"battle_end"}\n', encoding="utf-8")
    monkeypatch.setattr(sys, "argv", ["corpus_run.py", "--dry-run", "--from-file", str(src),
                                      "--out", str(out)])
    with pytest.raises(SystemExit) as e:
        cr.main()
    assert e.value.code == 2
    assert (out / "battle-001.jsonl").read_text(encoding="utf-8") == "keep"
    assert (out / "manifest.json").read_text(encoding="utf-8") == "[]"


def test_summarize_journal_tolerates_truncated_utf8(tmp_path):
    p = tmp_path / "j.jsonl"
    p.write_bytes('{"ev":"a"}\n{"ev":"b","t":"'.encode() + "ж".encode()[:1])
    counts, lines = cr.summarize_journal(p)
    assert lines == 2 and counts["a"] == 1


def test_port_log_report_skips_blank_and_non_dict_lines(tmp_path):
    p = tmp_path / "port.jsonl"
    p.write_text('{"ev":"start"}\n\n   \n[1,2]\n{bad\n{"ev":"end","reason":"x"}\n', encoding="utf-8")
    md, _ = plr.journal_report(p)
    assert "строк 4" in md
