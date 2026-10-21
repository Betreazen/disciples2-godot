"""Генерация в tmp из шаблона test_fast_battle.sg и разбор обратно тем же парсером."""
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import scenario_squads as ss  # noqa: E402
import sgfile as sg  # noqa: E402

TEMPLATE = ss.TEMPLATE.with_suffix(".sg.orig")
pytestmark = pytest.mark.skipif(not TEMPLATE.exists() or not ss.GLOBALS.exists(),
                                reason="нужны шаблон .orig и game-pristine/Globals")


@pytest.fixture(scope="module")
def tables():
    return ss.Tables()


def test_template_roundtrip_is_bytewise(tables):
    data = TEMPLATE.read_bytes()
    assert sg.Sg.loads(data).dumps() == data


@pytest.mark.parametrize("preset", sorted(ss.PRESETS))
def test_preset_generates_and_reads_back(tmp_path, tables, preset):
    own, enemy = (ss.PRESETS[preset][i].split(",") for i in range(2))
    out = tmp_path / f"{preset}.sg"
    scen = ss.build(TEMPLATE, own, enemy, tables)
    scen.write(out)
    ss.verify(TEMPLATE, out, own, enemy, tables)   # счётчик заголовка, позиции, составы
    back = sg.Sg.read(out)
    assert back.dumps() == out.read_bytes()
    hero = next(s for s in ss.roster(back, tables) if s[0] == ss.HERO_STACK)
    assert hero[1] == (1, 1)
    assert [m[1] for m in hero[3] if m[0] == 2] == ["g000uu0019"]   # лидер остался в клетке 2


def test_big_unit_takes_column(tables):
    cells = ss.place([-1, -1, 0, -1, -1, -1], ["g000uu0001", "g000uu0018"], 1, tables)
    assert cells == [2, 2, 0, -1, 1, -1]   # титан столбец (0,1), сквайр вперёд в 4


def test_ranged_goes_back(tables):
    cells = ss.place([-1, -1, 0, -1, -1, -1], ["g000uu0006"], 1, tables)
    assert cells == [-1, 1, 0, -1, -1, -1]


def test_overflow_rejected(tables):
    with pytest.raises(ValueError):
        ss.place([-1, -1, 0, -1, -1, -1], ["g000uu0018"] * 3, 1, tables)


def test_unknown_unit_rejected(tables):
    with pytest.raises(ValueError):
        ss.build(TEMPLATE, ["g000uu9999"], [], tables)
