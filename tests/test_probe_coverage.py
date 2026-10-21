"""Опись покрытия не ставит 0xCC посреди инструкции (19.10: четыре таких адреса роняли ход ИИ, `0x58AE84`)."""
import os
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import probe_coverage as pc  # noqa: E402

EXE = Path(os.environ.get("D2_GAME_DIR", str(ROOT.parent / "disciples-modding" / "game-pristine"))) / "Discipl2.exe"
needs_exe = pytest.mark.skipif(not EXE.exists(), reason=f"нет {EXE}")

# Середины инструкций, которые make-list счёл началами по случайному dword в .rdata: байт операнда `push [ebp+8]`
# (0x46464b ff 75 08), байты rel32 у `call` (0x58444d, 0x5c3a56) и у `jmp` (0x6b787a).
MID_INSTRUCTION = [0x46464D, 0x58444E, 0x5C3A58, 0x6B787B]
# Настоящие начала: сразу за таблицей переходов (0x4752bf, 0x5616f4), функтор кнопки «Построить» без прямого вызова (0x4af026).
REAL_STARTS = [0x4752BF, 0x5616F4, 0x4AF026]


@needs_exe
def test_mid_instruction_addresses_are_rejected():
    assert pc.not_instruction_starts(EXE, MID_INSTRUCTION + REAL_STARTS) == set(MID_INSTRUCTION)


@needs_exe
def test_shipped_list_has_no_mid_instruction_starts():
    funcs, _ = pc.load_functions(pc.FUNCTIONS)
    safe = [f for f, (_a, _c, s) in funcs.items() if s]
    assert pc.not_instruction_starts(EXE, safe) == set()


@needs_exe
def test_check_list_clears_safe_flag_of_bad_rows(tmp_path):
    tsv = tmp_path / "functions.tsv"
    tsv.write_text("# exe_size 1\n# address\tarea\tclass\tsafe\n0x46464d\tai\t\t1\n0x4752bf\tunowned\t\t1\n0x401000\tshared\t\t0\n",
                   encoding="utf-8")
    assert pc.check_list(EXE, tsv) == 0
    funcs, size = pc.load_functions(tsv)
    assert size == 1
    assert funcs[0x46464D][2] is False and funcs[0x4752BF][2] is True and funcs[0x401000][2] is False
