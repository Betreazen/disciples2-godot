"""f7_cli.py: каркас генераторов Ф7 — флаги, честная карта плюс порча по одной, отчёт по группам сторожей."""
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import f7_cli as cli  # noqa: E402


class FakeMap:
    def __init__(self, bad: str | None = None) -> None:
        self.bad = bad

    def write(self, path: Path) -> None:
        path.write_text(self.bad or "", encoding="utf-8")


def verify(path: Path, tables: object, quiet: bool = False) -> None:
    text = path.read_text(encoding="utf-8")
    if text == "caught":
        raise SystemExit("сторож: порча")


def test_parser_has_common_flags(tmp_path: Path) -> None:
    a = cli.parser("doc", out_dir=tmp_path, source=("--template", tmp_path / "t.sg")).parse_args(["--plan"])
    assert a.plan and not a.selftest and a.out_dir == tmp_path and a.template == tmp_path / "t.sg"
    assert a.scratch == cli.SCRATCH


def test_file_cases_reports_caught_and_missed_and_cleans_up(tmp_path: Path) -> None:
    cases = cli.file_cases(lambda t: FakeMap(), verify, lambda scen, what, t: FakeMap(what),
                           [("caught", "ловится"), ("missed", "пропущена")], tmp_path, "m99", tables=None)
    assert [name for name, _ in cases] == ["файл: ловится", "файл: пропущена"]
    assert cases[0][1] == "сторож: порча" and cases[1][1] is None
    assert not list(tmp_path.glob("*.sg"))


def test_file_cases_rejects_dishonest_map(tmp_path: Path) -> None:
    with pytest.raises(SystemExit, match="честная карта m99"):
        cli.file_cases(lambda t: FakeMap("caught"), verify, lambda s, w, t: s, [], tmp_path, "m99", tables=None)
    assert not list(tmp_path.glob("*.sg"))


def test_report_fails_on_missed_and_on_empty_group(capsys: pytest.CaptureFixture[str]) -> None:
    assert cli.report("m99", [("файл", [("a", "ловится"), ("b", None)])]) == 1
    assert "ПРОПУЩЕН" in capsys.readouterr().out
    assert cli.report("m99", [("файл", [("a", "ловится")])]) == 0
    with pytest.raises(SystemExit, match="не сделала ни одного сравнения"):
        cli.report("m99", [("пусто", [])])
