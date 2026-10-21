"""install.py не ставит в игру сборку зонда старше исходников (19.10: старая DLL от 30.09 затёрла зонд 7.5)."""
import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools" / "d2probe"))
import install  # noqa: E402


def _rev(*args: str) -> str:
    return subprocess.run(["git", "-C", str(install.REPO_ROOT), *args], capture_output=True, text=True,
                          check=True).stdout.strip()


def _dll(tmp_path: Path, payload: bytes) -> Path:
    p = tmp_path / "mss32.dll"
    p.write_bytes(b"MZ\x00\x00fmt\x000123456789abcdef\x00" + payload + b"\x00tail\x00")
    return p


def test_build_from_newest_sources_passes(tmp_path):
    src = install.last_source_commit()
    assert install.stale_build_reason(_dll(tmp_path, b"\x00" + src[:8].encode())) is None


def test_build_from_newer_commit_passes(tmp_path):
    assert install.stale_build_reason(_dll(tmp_path, b"\x00" + _rev("rev-parse", "--short=8", "HEAD").encode())) is None


def test_dirty_build_from_newest_sources_passes(tmp_path):
    src = install.last_source_commit()
    assert install.stale_build_reason(_dll(tmp_path, b"\x00" + src[:8].encode() + b"+dirty")) is None


def test_build_older_than_sources_is_refused(tmp_path):
    old = _rev("rev-parse", "--short=8", install.last_source_commit() + "~1")
    reason = install.stale_build_reason(_dll(tmp_path, b"\x00" + old.encode()))
    assert reason is not None and old in reason


def test_build_without_revision_is_refused(tmp_path):
    reason = install.stale_build_reason(_dll(tmp_path, b"\x00dev"))
    assert reason is not None and "build.ps1" in reason


def test_install_refuses_stale_build_before_touching_game(tmp_path, monkeypatch, capsys):
    game = tmp_path / "game"
    game.mkdir()
    (game / "mss32.dll").write_bytes(b"old")
    old = _rev("rev-parse", "--short=8", install.last_source_commit() + "~1")
    monkeypatch.setattr(install, "PROBE_DLL", _dll(tmp_path, b"\x00" + old.encode()))
    monkeypatch.setattr(install, "game_is_running", lambda: False)
    with pytest.raises(SystemExit) as e:
        install.cmd_install(game, autobattle=False, force_backup=False, vanilla_globals=False)
    assert e.value.code == 1
    assert "stale" in capsys.readouterr().err
    assert (game / "mss32.dll").read_bytes() == b"old"
    assert not (game / "probe").exists()
