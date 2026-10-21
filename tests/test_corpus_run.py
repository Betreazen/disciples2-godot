"""corpus_run.run_keep_game без игры: ap/gb подменены заглушками, зонд — файлом в tmp.

Серия из 3 боёв в одном процессе, второй бой «зависает» (нет battle_end) -> таймаут: журнал
первого процесса переносится с одним боем (status timeout), третий бой идёт новым процессом
и новым файлом (status ok). Проверяется: два файла, battles по battle_end, probe/ пуст,
kill вызван на каждый перенос, boot=True только при новом процессе.
"""
import json
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import corpus_run as cr  # noqa: E402

BATTLE = ('{"seq":1,"ev":"battle_start","units":[]}\n'
          '{"seq":2,"ev":"rng","n":50700,"max":10,"r":1}\n'
          '{"seq":3,"ev":"battle_end","winner":"x"}\n')


class FakeGame:
    """Один журнал на «процесс»: boot=True открывает новый файл; hang — бой без battle_end."""

    def __init__(self, probe: Path, hang_at: int) -> None:
        self.probe, self.hang_at, self.calls = probe, hang_at, []
        self.journal: Path | None = None
        self.n = 0

    # --- autoplay ---
    def kill_leftover_game(self) -> bool:
        self.calls.append("kill")
        return True

    def screenshot(self, path: Path) -> None:
        path.write_bytes(b"png")

    # --- goto_battle ---
    def open_to_battle(self, scenario: str | None = None, boot: bool = True) -> float:
        self.calls.append(f"open boot={boot}")
        if boot:
            self.journal = self.probe / f"proc-{len(self.calls)}.jsonl"
            self.journal.write_text("", encoding="utf-8")
        return 10.0

    def press_fast_battle(self, use_key: bool = False) -> None:
        self.n += 1
        assert self.journal is not None
        if self.n != self.hang_at:
            with self.journal.open("a", encoding="utf-8") as f:
                f.write(BATTLE)

    def leave_battle(self) -> None:
        self.calls.append("leave")


def test_keep_game_series_survives_timeout(tmp_path: Path, monkeypatch) -> None:
    probe, out = tmp_path / "probe", tmp_path / "out"
    probe.mkdir(), out.mkdir()
    game = FakeGame(probe, hang_at=2)
    monkeypatch.setattr(cr, "ap", game)
    monkeypatch.setattr(cr, "gb", game)
    monkeypatch.setattr(cr, "POLL_S", 0.01)
    rows: list[dict] = []
    totals: Counter = Counter()

    cr.run_keep_game(3, out, probe, timeout_s=0.1, settle_s=0.1, scenario="s", fast_key=True,
                     rows=rows, total_counts=totals)

    assert [(r["file"], r["status"], r["battles"]) for r in rows] == [
        ("battle-001.jsonl", "timeout", 1), ("battle-002.jsonl", "ok", 1)]
    assert list(probe.glob("*.jsonl")) == [], "журналы перенесены, probe/ пуст"
    assert (out / "fail-001.png").exists()
    assert json.loads((out / "manifest.json").read_text(encoding="utf-8")) == rows
    assert totals["battle_end"] == 2 and totals["battle_start"] == 2
    assert game.calls == ["kill", "open boot=True", "leave", "open boot=False", "kill",
                          "kill", "open boot=True", "leave", "kill"]


def test_wait_for_battle_counts_new_end_in_known_journal(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.setattr(cr, "POLL_S", 0.01)
    j = tmp_path / "j.jsonl"
    j.write_text(BATTLE, encoding="utf-8")
    assert cr.wait_for_battle(tmp_path, set(), 0.05, j, ends_before=1) is None, "второго battle_end нет"
    j.write_text(BATTLE * 2, encoding="utf-8")
    assert cr.wait_for_battle(tmp_path, set(), 0.05, j, ends_before=1) == j
    assert cr.wait_for_battle(tmp_path, set(), 0.05) == j, "режим перезапуска: новый файл"
