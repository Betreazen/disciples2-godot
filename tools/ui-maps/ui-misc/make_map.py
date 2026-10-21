#!/usr/bin/env python3
"""ui-misc: одна карта на окна DLG_STEAL_SPELL, DLG_R_C_RUIN, DLG_WAND_SCROLL, DLG_R_C_PLAYER,
DLG_ISO_LAND, DLG_R_C_TRANSFORM, DLG_TURNSUMMARY (и DLG_IMPORT_LEADER / DLG_VIEW_EXPLEAD на выборе
лорда, если героя уже экспортировали картой ui-end-win).

Запуск из корня disciples-godot:
    python tools/ui-maps/ui-misc/make_map.py --plan
    python tools/ui-maps/ui-misc/make_map.py            # запись в out/
    python tools/ui-maps/ui-misc/make_map.py --verify
    python tools/ui-maps/ui-misc/make_map.py --selftest
    python tools/ui-maps/ui-misc/make_map.py --install  # копия в game-dev/Exports

Основа — Fardale's Web (Эльфы + Империя), как у ai-q43-thief-warrior-lord; каркас p4a_common
(деревня эльфов и вор) только импортируется. Что добавлено и зачем (адреса — Discipl2.exe GOG 3.01):

  * деревня эльфов «Elf E» (9, 12) с одним стражем и вор Империи (13, 14) у её входа. «Украсть
    заклинание» предлагается Гильдмастеру против укрепления играбельной расы без визитёра, если у
    владельца есть заклинание, которого нет у игрока вора (0x5CDDE7). В ai-q43 его не было лишь
    потому, что у эльфов пустой список KS0002, — здесь эльфам даны четыре заклинания. Выбор
    действия открывает DLG_STEAL_SPELL (0x49AE42 -> 0x4A577F) до броска кражи;
  * руина Ordogg (33, 15) помечена разведанной Империей (список посетителей). Правый щелчок по
    такой руине — DLG_R_C_RUIN, по неразведанной или разграбленной — DLG_R_C_MISC (0x579510).
    Umwustt (7, 8) оставлена как была — для сравнения;
  * Архимаг Империи (32, 24): у него навык «Посохи и свитки» (L_WAND_SCROLL_USE, 7), в сумке
    свиток Air ward и посох Staff of Thunder. Кнопка «Активировать Посох или Свиток» на правой
    панели (BTN_WAND_SCROLL, 0x48F6C2) открывает DLG_WAND_SCROLL, только если выбран герой с этим
    навыком (0x48DE96, 0x8375A8), иначе — свиток X005TA0027;
  * одиночный нейтральный Крестьянин (33, 24) вплотную к Архимагу — бой в первый день: запись
    сводки хода (CEffectBattle -> CTurnSummary) и опыт.
"""
from __future__ import annotations

import importlib.util
import sys
from dataclasses import replace
from pathlib import Path

_spec = importlib.util.spec_from_file_location("ui_common", Path(__file__).resolve().parents[1] / "common.py")
uc = importlib.util.module_from_spec(_spec)
sys.modules["ui_common"] = uc
_spec.loader.exec_module(uc)  # type: ignore[union-attr]
import p4a_common as p4  # noqa: E402  (ставит основу Fardale's Web в каркас _batch1a)

pa, sg = p4.pa, uc.sg

NAME = "ui-misc"
BASE = p4.BASE
ARCHMAGE, PEASANT = "g000uu0021", "g000uu5101"
AIR_WARD_SCROLL, STAFF_OF_THUNDER = "G000IG5001", "G000IG6001"
L_WAND_SCROLL_USE = 7
ELF_SPELLS = ["G000SS0097", "G000SS0101", "G000SS0102", "G000SS0100"]  # Bramble, Swarm, Healing, Swift
RUIN_SCOUTED, RUIN_PLAIN = "S143RU0000", "S143RU0002"                  # Ordogg (33,15), Umwustt (7,8)
DESIGN = p4.Design(
    villages=(p4.Village("E", "elves", (9, 12), (p4.ELF_GUARD,), "Elf E"),),
    thieves=(p4.Thief("vor", (13, 14), "E", "«Украсть заклинание» -> DLG_STEAL_SPELL"),),
    banks={},
)
PARTIES = (
    pa.Party("mage", ARCHMAGE, (32, 24), items=(AIR_WARD_SCROLL, STAFF_OF_THUNDER)),
    pa.Party("target", PEASANT, (33, 24), owner="neutral"),
)
ADDED = DESIGN.added() + 2 * 2 + 2                  # деревня и вор; два отряда по юниту; две вещи
_BASE_SG = sg.Sg.read(BASE)
_OWN = p4.owners(_BASE_SG)
_PL = uc.players(_BASE_SG)
KS_ELVES, KS_EMPIRE = str(_PL[uc.ELVES]["KNOWN_ID"]), str(_PL[uc.EMPIRE]["KNOWN_ID"])
CHANGED = set(p4.changed_ids(_BASE_SG, DESIGN)) | {KS_ELVES, RUIN_SCOUTED}


def tables():
    return pa.Tables()


def build(base: sg.Sg, t, parties=PARTIES, spells=ELF_SPELLS, scout: bool = True) -> sg.Sg:
    scen = p4.build(base, t, DESIGN, NAME)
    scen, _got = pa.add_parties(scen, t, parties)
    scen = uc.set_known_spells(scen, KS_ELVES, list(spells))
    if scout:
        scen = uc.add_ruin_visitor(scen, RUIN_SCOUTED, _OWN["human"][0])
    return sg.set_scenario_name(scen, NAME)


def _stack_at(scen: sg.Sg, xy) -> list[dict]:
    return [s for s in (sg.parse_stack(r.body) for r in scen.find(sg.WHAT_STACK))
            if (int(s["POS_X"]), int(s["POS_Y"])) == tuple(xy)]


def checks(ch, scen: sg.Sg, base: sg.Sg, t) -> None:
    human, neutral, elves = _OWN["human"][0], _OWN["neutral"][0], _OWN["elves"][0]
    units = pa.cm.unit_rows(scen)
    items = pa.item_types(scen)
    # деревня и вор; сторожа p4a: ScenarioInfo (QTY_CITIES), террейн под деревней, сама деревня
    p4.check_info(ch, scen, base, DESIGN, NAME)
    p4.check_terrain(ch, scen, base, DESIGN)
    tmp = uc.Counter2(quiet=True)
    p4.check_villages(tmp, scen, base, t, DESIGN)      # его строка plan знает только деревню и воров
    for k, (ok, bad) in tmp.rows.items():
        if k != "plan":
            ch.rows.setdefault(k, [0, 0])
            ch.rows[k][0] += ok
            ch.rows[k][1] += bad
    ch.notes += [n for n in tmp.notes if not n.strip().startswith("plan ")]
    vrec = p4.new_villages(scen, base)
    stacks = pa.added_stacks(scen, base)
    want = [(x, y, r.obj_id) for v, r in zip(DESIGN.villages, vrec) for x, y in v.tiles()]
    want += [(int(s["POS_X"]), int(s["POS_Y"]), r.obj_id) for r, s in ((r, sg.parse_stack(r.body)) for r in stacks)]
    uc.check_plan_added(ch, scen, base, want)
    ch.count("plan", sorted((x, y) for x, y, _e in want[len(want) - len(stacks):])
             == sorted([tuple(DESIGN.thieves[0].xy)] + [tuple(p.xy) for p in PARTIES]), "отряды не на своих клетках")
    vid = p4.village_ids(scen, base, DESIGN.villages).get("E")
    ch.count("steal", vid is not None and p4.target_kind(scen, vid) == "fort_playable",
             f"деревня E {vid}: {vid and p4.target_kind(scen, vid)}")
    if vid:
        vil = p4.city.parse_village(pa.cm.record(scen, vid).body, True)
        ch.count("steal", vil["OWNER"] == elves and vil["STACK"] == sg.NULL_ID,
                 f"деревня E: владелец {vil['OWNER']} визитёр {vil['STACK']}")
    th = DESIGN.thieves[0]
    got = _stack_at(scen, th.xy)
    ch.count("steal", len(got) == 1 and got[0]["OWNER"] == human
             and units[got[0]["LEADER_ID"]][0] == p4.THIEF, f"вор в {th.xy}: {got}")
    if vid:
        ch.count("steal", pa.cm.cheb(th.xy, p4.target_entrance(scen, vid)) == 1, "вор не у входа деревни")
    known = uc.parse_known(pa.cm.record(scen, KS_ELVES).body)
    mine = set(uc.parse_known(pa.cm.record(scen, KS_EMPIRE).body))
    ch.count("steal", known == ELF_SPELLS and not (set(known) & mine), f"заклинания эльфов {known}, Империи {mine}")
    ch.count("steal", all(s.lower() in {k.lower() for k in t.spells} for s in known), "заклинания нет в Gspells")
    # руины
    f, vis, _a, _b = uc.parse_ruin(pa.cm.record(scen, RUIN_SCOUTED).body)
    ch.count("ruin", vis == [(RUIN_SCOUTED, human)] and f["LOOTER"] == sg.NULL_ID,
             f"{RUIN_SCOUTED}: посетители {vis}, грабитель {f['LOOTER']}")
    f2, vis2, _a, _b = uc.parse_ruin(pa.cm.record(scen, RUIN_PLAIN).body)
    ch.count("ruin", vis2 == [] and f2["LOOTER"] == sg.NULL_ID, f"{RUIN_PLAIN}: посетители {vis2}")
    # Архимаг с посохом и свитком, цель вплотную
    uc.check_map_leaders(ch, t, PARTIES)
    mage, tgt = PARTIES
    m = _stack_at(scen, mage.xy)
    ch.count("wand", len(m) == 1 and m[0]["OWNER"] == human and units[m[0]["LEADER_ID"]][0] == ARCHMAGE,
             f"Архимаг в {mage.xy}: {m}")
    if len(m) == 1:
        inv = sorted(items.get(i) for i in m[0]["ITEMS"])
        ch.count("wand", inv == sorted([AIR_WARD_SCROLL, STAFF_OF_THUNDER]), f"сумка {inv}")
        ch.count("wand", all(m[0][k] == sg.NULL_ID for k in sg._ITEM_SLOTS), "вещь надета, а должна лежать в сумке")
        ch.count("wand", {t.item_cat(AIR_WARD_SCROLL), t.item_cat(STAFF_OF_THUNDER)} == {8, 9}, "не свиток и посох")
    ch.count("wand", L_WAND_SCROLL_USE in t.abilities(ARCHMAGE), "у Архимага нет навыка 7")
    n = _stack_at(scen, tgt.xy)
    ch.count("wand", len(n) == 1 and n[0]["OWNER"] == neutral and n[0]["ORDER"] == pa.ORDER_STAND
             and pa.cm.cheb(mage.xy, tgt.xy) == 1, f"цель в {tgt.xy}: {n}")
    ccells = uc.Counter2(quiet=True)
    pa.check_cells(ccells, base, list(PARTIES) + [th.party()], {frozenset(("mage", "target"))})
    for k, (ok, bad) in ccells.rows.items():
        ch.rows.setdefault("cells", [0, 0])
        ch.rows["cells"][0] += ok
        ch.rows["cells"][1] += bad
    ch.notes += ccells.notes
    plan = {(x, y) for x, y, _e in sg.parse_plan(scen.one(sg.WHAT_PLAN).body)[1]}
    ch.count("cells", all(tuple(p.xy) in plan for p in PARTIES) and tuple(th.xy) in plan, "отряд не в плане")


def sabotages(base: sg.Sg, t):
    return [
        ("у эльфов нет заклинаний", "steal", build(base, t, spells=[]).dumps()),
        ("руина не разведана", "ruin", build(base, t, scout=False).dumps()),
        ("у героя нет навыка посохов (Рыцарь на пегасе)", "wand",
         build(base, t, parties=(replace(PARTIES[0], leader="g000uu0019"), PARTIES[1])).dumps()),
        ("свиток надет, а не в сумке", "wand",
         build(base, t, parties=(replace(PARTIES[0], slots={"ARTIFACT1": 0}), PARTIES[1])).dumps()),
    ]


def print_plan(base: sg.Sg, t) -> None:
    print(f"  деревня эльфов Elf E {DESIGN.villages[0].xy}, вор {DESIGN.thieves[0].xy}")
    print(f"  заклинания эльфов {KS_ELVES}: {ELF_SPELLS}")
    print(f"  руина {RUIN_SCOUTED} разведана игроком {_OWN['human'][0]}; {RUIN_PLAIN} — нет")
    for p in PARTIES:
        print(f"  {p.role}: {t.name(p.leader)} в {p.xy}, владелец {p.owner}, сумка {list(p.items)}")


if __name__ == "__main__":
    sys.exit(uc.run(sys.modules[__name__]))
