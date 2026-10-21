#!/usr/bin/env python3
"""Девять карт Ф7, которым нужен готовый отряд, вещи, лавка или сосед: M4, M6, M8, M9, M10
и вторая волна M11…M14.

    python tools/make_f7_patch_maps.py                # собрать все девять в game-dev/Exports
    python tools/make_f7_patch_maps.py --only m8      # одну (можно повторять)
    python tools/make_f7_patch_maps.py --plan         # только расчёт и сторожа, без записи
    python tools/make_f7_patch_maps.py --selftest     # порча карт и профилей + живые сохранения

ТЗ — `disciples-modding/docs/superpowers/plans/2026-09-09-f7-run-scenarios.md`, сценарии
S7, S9, S11…S15, плюс S4, S6, S10 и S14 второй волной. Соседний генератор
`tools/make_f7_maps.py` собирает M2, M3, M5, M7.

**Вторая волна, 09.09.2026.** M11 добавляет лавку (Тренера) — писатель берётся готовым из
`make_sites_map`, он сверен с ванилью на 150 объектах. M12 добавляет гарнизон города и
проверку простора вокруг него. M13 — десять воров и две мишени. M14 — ВТОРОГО ИГРАБЕЛЬНОГО
игрока (`tools/sg_second_player.py`): без него записи `CMidDiplomacy` не двигаются вовсе, и
`D_ROD`/`D_NOBLE` непроверяемы. Из-за второй столицы план карты собирается здесь целиком, а
не через `c4.rebuild_plan` — тот берёт `one(CCapital)`.

**Имя файла историческое.** До 09.09.2026 эти пять карт доводились ЗАПЛАТОЙ СОХРАНЕНИЯ
(`patch_f6_save.py --profile`). На живом прогоне заплата не дала ничего, и её половина Ф7
удалена; здесь всё задаётся СЦЕНАРИЕМ. Файл не переименован: на него ссылаются памятки в
соседнем репозитории, и слово «patch» в имени — единственное, что от заплаты осталось.
Состав, геометрия и сторожа, которым файл не нужен, живут в `tools/f7_plan.py` — деление то
же, что у пары `items_plan.py` / `make_items_map.py`.

**Что провалилось и почему (прогон `corpus/20260909-passive`, 09.09.2026).**
Стартовый отряд игрока сценарием не задаётся вовсе: при выборе лорда движок выбрасывает стек,
лежащий В СТОЛИЦЕ, и кладёт свой. По двенадцати сохранениям первого хода лидер — чистая
функция лорда, а не карты: `G000LR0001` (Воитель) -> Pegasus Knight, `G000LR0002` (Повелитель
Воров) -> Ranger, `G000LR0003` (Маг) -> Archmage; плюс до трёх солдат по умолчанию. Наш
Pegasus Knight из сценария уцелел ровно там, где пользователь взял Воителя.
Заплата чинила это ПОСЛЕ выбора лорда, но требовала ручного `Q` и ручной команды между
загрузкой и игрой: за весь прогон `QuickSave.sg` не переписывался ни разу. А на лордах
Воителя и Мага она бы ещё и отказалась работать — стартовый отряд там из четырёх юнитов,
заплата же требовала одного.

**Что переживает выбор лорда — и чем это доказано (33 ванильные карты `Exports-archive`).**

* ЛИШНИЕ отряды игрока, стоящие НА КАРТЕ. В ванили их до десяти на расу (`Flight`), и ровно
  один на расу лежит в городе — тот самый, который движок и пересоздаёт. Отсюда решение
  пользователя для M4: отряд стоит рядом со столицей, лорд его не трогает, а стартового
  героя пользователь выводит и заводит туда этот.
* РАНЕНЫЕ юниты (`HP` меньше `HIT_POINT`): 67 штук в 15 ванильных картах.
* ИЗВЕСТНЫЕ ЗАКЛИНАНИЯ игрока (`CPlayerKnownSpells`): `The Seals` выдаёт по пять на расу.
* МЕШКИ с вещами на карте (`CMidBag`): 537 штук в 31 карте, каждый одной клеткой в плане.
* Казна, грунт, нейтралы, города и потолки `ScenarioInfo` — это подтвердил сам прогон: на M4
  казна 2000 - 300 (Храм `g000bb0023`) + 100 (доход) = 1800, ровно как в сохранении.

**ПАВШИЙ юнит (`HP = 0`) сценарием не проверен.** В 33 ванильных картах таких нет ни одного
(раненых 67, павших 0). Признак взят из движка (`isAlive` читается как `hitPoints > 0`,
`0x5E98AD`), а формат записи `CMidUnit` у сценария и сохранения общий, поэтому ставим — но
это единственное место карты M4, которое может не сработать, и в памятке оно названо.

**`MAX_UNIT = 5` у всех пяти.** Вопреки имени это потолок уровня ПОСТРОЙКИ (`unitMaxTier`,
`cities.md`, ранг 1); у шаблона стоит 2 — отсюда старая жалоба «не даёт построить Белую
башню». Заодно **`MAX_LEADER` = 99**: это `CScenarioInfo::leaderMaxLevel` (геттер `0x5E3A26`,
проверка `if (level >= leaderMaxLevel)` в `0x5D89C6`), и при единице прогон M8 провалился бы
молча — герой не получил бы второго уровня, а с ним и окна выбора навыка.

**Жезлы (M6, M10) не ставятся сценарием** — их ставит игрок, и правила заданы движком: только
равнина, не ближе клетки к городу (`CVisitorCreateRod::canApply` 0x6137FE), между жезлами
минимум **3** по Чебышёву (серверная проверка 0x5D9BDB). Жезлоносец каждой площадки стоит на
клетку ЗАПАДНЕЕ первого жезла: клетка жезла остаётся пустой в плане, а герой делает шаг.

**Жил (кристаллов) нет ни на одной карте.** Доход идёт с жил; карта без жил даёт нулевой
доход, и тогда любое изменение казны — это ровно трата. Для M4, где меряются цены лечения,
это несущее требование.
"""
from __future__ import annotations

import argparse
import contextlib
import io
import sys
from dataclasses import replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import sgfile as sg  # noqa: E402
import make_c4_map as c4  # noqa: E402
import scenario_c4 as sc  # noqa: E402
import scenario_squads as sq  # noqa: E402
import make_city_map as city  # noqa: E402  (запись CMidVillage и правка ScenarioInfo — оттуда)
import make_items_map as mi  # noqa: E402   (WHAT_ITEM/WHAT_TALISMAN, заряды талисманов)
import make_f7_maps as mf  # noqa: E402    (add_units — свежие CMidUnit под гарнизон города)
import make_sites_map as ms  # noqa: E402  (писатель лавок, сверенный с ванилью байт в байт)
import f7_plan as fp  # noqa: E402       (профили, геометрия и сторожа, которым файл не нужен)
import sg_second_player as sp  # noqa: E402  (запись второго играбельного игрока и её проверка)
from make_arena_map import set_bank, set_leader_cell  # noqa: E402
from sg_second_player import (  # noqa: E402,F401
    WHAT_DIPLO, WHAT_FOG, WHAT_KNOWN, WHAT_BUILDS,
    add_second_player, diplomacy_pairs, relation_word, second_player, verify_second)

MODDING = Path(__file__).resolve().parents[2] / "disciples-modding"
# Игра читает сценарии ТОЛЬКО отсюда: `game-dev/scenarios/` — подсобка, на этом уже спотыкались.
EXPORTS = MODDING / "game-dev" / "Exports"
# Сохранения живого прогона Ф7: на них проверяется, что сторож поймал бы тогдашнюю беду.
LIVE_RUN = Path(__file__).resolve().parents[1] / "corpus" / "20260909-passive"

WHAT_BAG = ".?AVCMidBag@@"


# ------------------------------------------------------- чтение юнитов и отрядов

def unit_hp(rec: sg.Record) -> int:
    """Поле `HP` тела `CMidUnit`. Модификаторы и `DYNLEVEL` необязательны — движок пишет их не
    всегда (в ванильных картах есть тела без `DYNLEVEL`), поэтому пропускаем по факту."""
    c = sg.Cursor(rec.body)
    c.fields(sg.LAYOUT_UNIT)
    for _ in range(c.count()):
        c.str("MODIF_ID")
    c.int("CREATION")
    c.str("NAME_TXT")
    c.bool("TRANSF")
    if rec.body[c.pos:c.pos + 8] == b"DYNLEVEL":
        c.bool("DYNLEVEL")
    return c.int("HP")


def unit_types(scen: sg.Sg) -> dict[str, str]:
    return {r.obj_id.upper(): str(sg.fields(r.body, sg.LAYOUT_UNIT)["TYPE"]).lower()
            for r in scen.find(sg.WHAT_UNIT)}


def players(scen: sg.Sg) -> tuple[dict, dict]:
    got = [sg.fields(r.body, sg.LAYOUT_PLAYER) for r in scen.find(sg.WHAT_PLAYER)]
    # Игрок прогона ищется по РАСЕ, а не «первый не нейтральный»: на M14 не нейтральных двое.
    human = next(p for p in got if p["RACE_ID"].upper() == sc.RACE_EMPIRE.upper())
    neutral = next(p for p in got if p["RACE_ID"] == sc.RACE_NEUTRAL)
    return human, neutral


def own_stacks(scen: sg.Sg) -> list[sg.Record]:
    owner = players(scen)[0]["PLAYER_ID"]
    return [r for r in scen.find(sg.WHAT_STACK) if sg.parse_stack(r.body)["OWNER"] == owner]


# ---------------------------------------------------------------------- сборка

def _same(scen: sg.Sg, rec: sg.Record) -> sg.Record:
    return next(r for r in scen.records if r.obj_id == rec.obj_id)


def village_xy(rec: sg.Record) -> tuple[int, int]:
    f = sg.fields(rec.body, sg.LAYOUT_CAPITAL)
    return int(f["POS_X"]), int(f["POS_Y"])


def bag_body(bag_id: str, xy: tuple[int, int], items: list[str]) -> bytes:
    """Тело `CMidBag` — как в ванильных картах: место, картинка, приоритет ИИ, список вещей."""
    return (sg.f_str("BAG_ID", bag_id) + sg.f_int("POS_X", xy[0]) + sg.f_int("POS_Y", xy[1])
            + sg.f_int("IMAGE", 0) + sg.f_int("AIPRIORITY", 3)
            + sg.f_count(bag_id, len(items))
            + b"".join(sg.f_str("ITEM_ID", i) for i in items))


def add_bag(scen: sg.Sg, xy: tuple[int, int], types: list[str]) -> sg.Sg:
    """Мешок на карте: запись `CMidItem` на каждую вещь плюс сам мешок. Заряды талисманов
    заводятся сразу — без них талисман в игре пустой."""
    item_ids = scen.next_ids("IM", len(types))
    scen = scen.appended(*[
        sg.Record(mi.WHAT_ITEM, iid, sg.f_str("ITEM_ID", iid) + sg.f_str("ITEM_TYPE", tp))
        for iid, tp in zip(item_ids, types)])
    charges = [(iid, fp.TALISMAN_CHARGES) for iid, tp in zip(item_ids, types)
               if tp.upper() == fp.ITEM_TALISMAN.upper()]
    if charges:
        scen = mi.set_charges(scen, mi.parse_charges(scen.one(mi.WHAT_TALISMAN).body) + charges)
    bag_id = scen.next_ids("BG")[0]
    return scen.appended(sg.Record(WHAT_BAG, bag_id, bag_body(bag_id, xy, item_ids)))


def add_party(scen: sg.Sg, party: dict, owner: str, subrace: str, t: sq.Tables) -> sg.Sg:
    """Отряд игрока на карте: `CMidUnit` лидера с его раной, `CMidStack`, солдаты и их раны.
    Опыт — необязательные ключи `party["leader_xp"]` и `party["units_xp"]` (список по солдатам,
    None — ноль); без них тела юнитов прежние байт в байт."""
    row = t.unit(party["leader"])
    uid = scen.next_ids("UN")[0]
    lead_hp = party.get("leader_hp")
    scen = scen.appended(sg.Record(sg.WHAT_UNIT, uid, sg.unit_body(
        uid, party["leader"].upper(), int(row["LEVEL"]),
        int(row["HIT_POINT"]) if lead_hp is None else int(lead_hp),
        int(party.get("leader_xp") or 0))))
    sid = scen.next_ids("KC")[0]
    stack = sg.Record(sg.WHAT_STACK, sid, sg.stack_body(
        sid, [uid], [0, -1, -1, -1, -1, -1], uid, party["xy"][0], party["xy"][1], owner, subrace,
        move=int(row["MOVE"] or 0)))
    scen = set_leader_cell(scen.appended(stack), stack, party["cell"])
    if not party["units"]:
        return scen
    unit_ids = scen.next_ids("UN", len(party["units"]))
    scen, _ = sq.add_soldiers(scen, _same(scen, stack), [u for u, _ in party["units"]],
                              unit_ids, t)
    xps = party.get("units_xp") or [None] * len(unit_ids)
    for unit_id, (impl, hp), xp in zip(unit_ids, party["units"], xps):
        if hp is None and not xp:
            continue
        rec = next(r for r in scen.find(sg.WHAT_UNIT) if r.obj_id == unit_id)
        if hp is None:                       # опыт без раны: здоровье полное, как у add_soldiers
            hp = int(t.unit(impl)["HIT_POINT"])
        scen = scen.with_record(rec, replace(rec, body=sg.unit_body(
            unit_id, impl.upper(), int(t.unit(impl)["LEVEL"]), hp, int(xp or 0))))
    return scen


def add_sites(scen: sg.Sg, prof: dict) -> sg.Sg:
    """Лавки профиля. Писатель — из `make_sites_map`, он сверен с ванилью байт в байт на 150
    объектах; свой писать заново незачем, а Тренер там единственный класс без нагрузки."""
    specs = tuple(ms.SiteSpec(ms.WHAT_TRAINER, 0, s["title"], "Кого тренировать?", tuple(s["xy"]))
                  for s in prof.get("sites", ()))
    return ms.add_sites(scen, specs)[0] if specs else scen


def set_known_spells(scen: sg.Sg, player: dict, spells: list[str]) -> sg.Sg:
    """`CPlayerKnownSpells` игрока — счётчик и список. Ваниль (`The Seals`) выдаёт так же."""
    rec = next(r for r in scen.records if r.obj_id == player["KNOWN_ID"])
    body = sg.f_count(rec.obj_id, len(spells)) + b"".join(
        sg.f_str("SPELL_ID", s) for s in spells)
    return scen.with_record(rec, replace(rec, body=body))


def known_spells(scen: sg.Sg, player: dict) -> list[str]:
    rec = next(r for r in scen.records if r.obj_id == player["KNOWN_ID"])
    c = sg.Cursor(rec.body)
    return [c.str("SPELL_ID") for _ in range(c.count())]


def rebuild_plan(scen: sg.Sg) -> sg.Sg:
    """План собирается целиком здесь, а не через `c4.rebuild_plan`: тот берёт `one(CCapital)`,
    а на M14 столиц две. Пятна: столица 5x5, город 4x4, лавка 3x3, отряд и мешок по клетке."""
    entries = [(x, y, r.obj_id) for r in scen.find(sg.WHAT_CAPITAL)
               for x, y in fp.capital_tiles_at(village_xy(r))]
    entries += [(x, y, r.obj_id) for r in scen.find(city.WHAT_VILLAGE)
                for x, y in city.village_tiles(village_xy(r))]
    entries += [(x, y, r.obj_id) for r in scen.find("MidSite")
                for x, y in fp.site_tiles(site_xy(r))]
    entries += [(int(sg.parse_stack(r.body)["POS_X"]), int(sg.parse_stack(r.body)["POS_Y"]),
                 r.obj_id) for r in scen.find(sg.WHAT_STACK)]
    entries += [(int(f["POS_X"]), int(f["POS_Y"]), r.obj_id)
                for r in scen.find(c4.WHAT_CRYSTAL)
                for f in [sg.fields(r.body, "CRYSTAL_ID:s RESOURCE:i POS_X:i POS_Y:i")]]
    entries += [(int(f["POS_X"]), int(f["POS_Y"]), r.obj_id)
                for r in scen.find(WHAT_BAG)
                for f in [sg.fields(r.body, "BAG_ID:s POS_X:i POS_Y:i")]]
    rec = scen.one(sg.WHAT_PLAN)
    size, _ = sg.parse_plan(rec.body)
    return scen.with_record(rec, replace(rec, body=sg.plan_body(rec.obj_id, size, entries)))


def site_xy(rec: sg.Record) -> tuple[int, int]:
    f = sg.fields(rec.body, ms.SITE_HEAD.split(" VISITER:s")[0])
    return int(f["POS_X"]), int(f["POS_Y"])


def add_town(scen: sg.Sg, prof: dict, neutral: dict, second: str | None,
             second_sub: str | None) -> sg.Sg:
    """Города профиля (восемь карт задают один, M16 — пять): владелец нейтральный или соседний
    игрок, гарнизон — свежие `CMidUnit`."""
    for v in fp.villages(prof):
        if v.get("owner") == "second":
            owner, subrace = second, second_sub
        else:
            owner = neutral["PLAYER_ID"]
            subrace = city.neutral_subrace(scen, owner, city.NEUTRAL_SUBRACE)
        scen, garrison = mf.add_units(scen, tuple(v.get("garrison", ())))
        scen = city.add_village(scen, owner, subrace, v["xy"], v["tier"], v["name"], garrison)[0]
    return scen


def place_neutrals(scen: sg.Sg, prof: dict, key: str, neutral: dict, t: sq.Tables) -> sg.Sg:
    """Оставшиеся отряды шаблона переезжают на места мишеней и меняют тип лидера."""
    kept = [r for r in scen.find(sg.WHAT_STACK)
            if sg.parse_stack(r.body)["OWNER"] == neutral["PLAYER_ID"]]
    if len(kept) != len(prof["neutrals"]):
        raise SystemExit("%s: в шаблоне осталось %d нейтральных отрядов, нужно %d"
                         % (key, len(kept), len(prof["neutrals"])))
    for rec, b in zip(kept, prof["neutrals"]):
        scen = c4.move_stack(scen, rec, *b["xy"])
        rec = _same(scen, rec)
        scen = c4.set_hero_type(scen, rec, b["leader"])
        if b["soldiers"]:
            rec = _same(scen, rec)
            scen, _ = sq.add_soldiers(scen, rec, list(b["soldiers"]),
                                      scen.next_ids("UN", len(b["soldiers"])), t)
    return scen


def build(template: Path, key: str, t: sq.Tables) -> tuple[sg.Sg, dict]:
    prof = fp.profile(key)
    scen = sg.Sg.read(template)
    size = len(sg.tile_grid(scen))
    scen, changed = c4.set_terrain(scen, fp.plain_tiles(size, prof))
    scen = c4.move_capital(scen, *fp.CAP)      # до второй столицы: он берёт one(CCapital)

    hero = next(r for r in scen.find(sg.WHAT_STACK)
                if sg.parse_stack(r.body)["INSIDE"] != sg.NULL_ID)
    scen = c4.move_stack(scen, hero, *fp.CAP)
    scen, dropped = c4.drop_neutral_stacks(scen, keep=len(prof["neutrals"]))

    human, neutral = players(scen)
    scen = place_neutrals(scen, prof, key, neutral, t)

    own = sg.parse_stack(_same(scen, hero).body)
    for party in prof["parties"]:
        scen = add_party(scen, party, own["OWNER"], own["SUBRACE"], t)
    for bag in prof["bags"]:
        scen = add_bag(scen, bag["xy"], bag["items"])
    scen = add_sites(scen, prof)
    scen = set_known_spells(scen, human, prof["spells"])

    second = second_sub = None
    if prof.get("second"):
        scen, second, second_sub = add_second_player(scen, prof["second"])
    scen = add_town(scen, prof, neutral, second, second_sub)
    scen = city.set_qty_cities(scen, len(scen.find(city.WHAT_VILLAGE)))
    scen = set_bank(scen, human["PLAYER_ID"], prof["bank"])
    for field, value in (("MAX_UNIT", fp.MAX_UNIT_WANTED), ("MAX_LEADER", fp.MAX_LEADER_WANTED),
                         ("MAX_SPELL", fp.MAX_SPELL_WANTED), ("MAX_CITY", fp.MAX_CITY_WANTED)):
        scen = city.set_info_int(scen, field, value)
    scen = rebuild_plan(scen)
    scen = sg.set_scenario_name(scen, prof["name"])
    return scen, {"клеток перерисовано": changed, "отрядов удалено": len(dropped),
                  "нейтралов": len(prof["neutrals"]), "отрядов игрока": len(prof["parties"]),
                  "мешков": len(prof["bags"]), "заклинаний": len(prof["spells"]),
                  "лавок": len(prof.get("sites", ())),
                  "городов": len(scen.find(city.WHAT_VILLAGE)),
                  "столиц": len(scen.find(sg.WHAT_CAPITAL)), "записей": len(scen.records)}


# ------------------------------------------------------------------- проверка

def check_header(scen: sg.Sg, prof: dict, key: str) -> None:
    """Потолки, оба места с именем карты, казна и грунт."""
    fp.check_scenario_limits(city.info_fields(scen), key)
    if sg.scenario_name(scen) != prof["name"]:
        raise SystemExit("%s: ScenarioInfo.NAME %r, ожидалось %r"
                         % (key, sg.scenario_name(scen), prof["name"]))
    raw = sg.header_name(scen)
    if raw != prof["name"]:
        raise SystemExit("%s: имя в заголовке %r (список игры берёт именно его), ожидалось %r"
                         % (key, raw, prof["name"]))
    human, _ = players(scen)
    if human["BANK"] != prof["bank"]:
        raise SystemExit("%s: казна %s, ожидалась %s" % (key, human["BANK"], prof["bank"]))
    grid = sg.tile_grid(scen)
    want = fp.plain_tiles(len(grid), prof)
    bad = [xy for xy, tile in want.items() if grid[xy[1]][xy[0]] != tile[0]]
    if bad:
        raise SystemExit("%s: грунт разошёлся в %d клетках, первые %s" % (key, len(bad), bad[:5]))
    own_land = sum(1 for row in grid for tile in row if tile & 7 == 1)
    if own_land != len(c4.capital_tiles()):
        raise SystemExit("%s: своей земли на старте %d клеток, а должно быть ровно пятно "
                         "столицы (%d): иначе прирост земли не с чем сравнивать"
                         % (key, own_land, len(c4.capital_tiles())))


def _party_units(s: dict, types: dict) -> list[tuple[str, str]]:
    """(id записи, тип) солдат отряда — лидер исключён, повторы клеток крупных юнитов сняты."""
    out, seen = [], set()
    for k in range(6):
        uid = s[f"UNIT_{k}"]
        if uid == sg.NULL_ID or uid.upper() == s["LEADER_ID"].upper() or uid in seen:
            continue
        seen.add(uid)
        out.append((uid.upper(), types[uid.upper()]))
    return out


def verify_parties(scen: sg.Sg, prof: dict, key: str, t: sq.Tables) -> None:
    """Отряды игрока в файле: стоят где надо, лидер того типа, состав и КАЖДОЕ здоровье."""
    types = unit_types(scen)
    units = {r.obj_id.upper(): r for r in scen.find(sg.WHAT_UNIT)}
    by_pos = {}
    for r in own_stacks(scen):
        s = sg.parse_stack(r.body)
        by_pos[(int(s["POS_X"]), int(s["POS_Y"]))] = s
    for p in prof["parties"]:
        s = by_pos.get(tuple(p["xy"]))
        if s is None:
            raise SystemExit("%s: в клетке %s нет отряда игрока (%s) — отряды игрока стоят в %s"
                             % (key, tuple(p["xy"]), p["why"], sorted(by_pos)))
        if types[s["LEADER_ID"].upper()] != p["leader"]:
            raise SystemExit("%s: отряд %s ведёт %s, ожидался %s"
                             % (key, tuple(p["xy"]), types[s["LEADER_ID"].upper()], p["leader"]))
        lead_hp, got_hp = p.get("leader_hp"), unit_hp(units[s["LEADER_ID"].upper()])
        if lead_hp is not None and got_hp != int(lead_hp):
            raise SystemExit("%s: у ЛИДЕРА отряда %s здоровье %d, ожидалась рана %d"
                             % (key, tuple(p["xy"]), got_hp, int(lead_hp)))
        got = _party_units(s, types)
        want = list(p["units"])
        if sorted(impl for _, impl in got) != sorted(u for u, _ in want):
            raise SystemExit("%s: состав отряда %s — %s, ожидался %s"
                             % (key, tuple(p["xy"]), [i for _, i in got], [u for u, _ in want]))
        for uid, impl in got:
            want_hp = next(h for u, h in want if u == impl)
            want.remove((impl, want_hp))
            expect = int(t.unit(impl)["HIT_POINT"]) if want_hp is None else want_hp
            hp = unit_hp(units[uid])
            if hp != expect:
                raise SystemExit("%s: у %s в отряде %s HP %d, ожидалось %d%s"
                                 % (key, impl, tuple(p["xy"]), hp, expect,
                                    " (это ПАВШИЙ юнит, HP обязан быть 0)"
                                    if want_hp == 0 else ""))


def verify_bags(scen: sg.Sg, prof: dict, key: str) -> None:
    """Мешки: стоят где надо, лежит в них ровно то, и у талисманов есть заряды."""
    kinds = mi.item_types(scen)
    got = {}
    for r in scen.find(WHAT_BAG):
        c = sg.Cursor(r.body)
        f = c.fields("BAG_ID:s POS_X:i POS_Y:i IMAGE:i AIPRIORITY:i")
        got[(int(f["POS_X"]), int(f["POS_Y"]))] = [c.str("ITEM_ID") for _ in range(c.count())]
    charges = dict(mi.parse_charges(scen.one(mi.WHAT_TALISMAN).body))
    for b in prof["bags"]:
        ids = got.get(tuple(b["xy"]))
        if ids is None:
            raise SystemExit("%s: в клетке %s нет мешка (%s) — мешки стоят в %s"
                             % (key, tuple(b["xy"]), b["why"], sorted(got)))
        if sorted(kinds[i].upper() for i in ids) != sorted(i.upper() for i in b["items"]):
            raise SystemExit("%s: в мешке %s лежит %s, ожидалось %s"
                             % (key, tuple(b["xy"]), [kinds[i] for i in ids], b["items"]))
        for i in [i for i in ids if kinds[i].upper() == fp.ITEM_TALISMAN.upper()]:
            if charges.get(i) != fp.TALISMAN_CHARGES:
                raise SystemExit("%s: у талисмана %s зарядов %s, ожидалось %d"
                                 % (key, i, charges.get(i), fp.TALISMAN_CHARGES))


def verify_spells(scen: sg.Sg, prof: dict, key: str) -> None:
    human, _ = players(scen)
    have = [s.upper() for s in known_spells(scen, human)]
    lost = [s for s in prof["spells"] if s.upper() not in have]
    if lost:
        raise SystemExit("%s: игроку не выданы заклинания %s — известных всего %d"
                         % (key, ", ".join(lost), len(have)))


def check_plan_refs(scen: sg.Sg, prof: dict, key: str) -> None:
    """План карты: без висячих ссылок, клетки жезлов свободны, всё поставленное внесено."""
    ids = {r.obj_id for r in scen.records}
    _, plan = sg.parse_plan(scen.one(sg.WHAT_PLAN).body)
    dangling = sorted({e for _, _, e in plan} - ids)
    if dangling:
        raise SystemExit("%s: в плане ссылки на удалённые записи: %s" % (key, dangling))
    occupied = {(x, y) for x, y, _ in plan}
    for plot, cells in prof["rods"].items():
        busy = [c for c in cells if c in occupied]
        if busy:
            raise SystemExit("%s: клетки жезлов площадки %s заняты объектом карты %s — движок "
                             "откажет в постановке" % (key, plot, busy))
    for xy, what in fp.occupied_cells(prof):
        if xy not in occupied:
            raise SystemExit("%s: %s в %s не внесён в план карты — движок его не найдёт"
                             % (key, what, xy))
    for r in scen.find(sg.WHAT_STACK):
        s = sg.parse_stack(r.body)
        for ref in [s[f"UNIT_{k}"] for k in range(6)] + list(s["ITEMS"]):
            if ref != sg.NULL_ID and ref not in ids:
                raise SystemExit("%s: %s ссылается на удалённую запись %s" % (key, r.obj_id, ref))


def verify_town(scen: sg.Sg, prof: dict, key: str, neutral: dict, types: dict) -> None:
    """Города: их столько же, стоят где обещано, владелец тот, гарнизон тот."""
    found = scen.find(city.WHAT_VILLAGE)
    wanted = fp.villages(prof)
    if len(found) != len(wanted):
        raise SystemExit("%s: городов %d, ожидалось %d" % (key, len(found), len(wanted)))
    by_pos = {tuple(v["xy"]): v for v in wanted}
    for r in found:
        if (want := by_pos.get(village_xy(r))) is None:
            raise SystemExit("%s: город в %s не обещан профилем — обещаны %s"
                             % (key, village_xy(r), sorted(by_pos)))
        v = city.parse_village(r.body, city._has_p_o_el(scen))
        owner = v["OWNER"]
        if want.get("owner") == "second":
            second = second_player(scen, prof["second"])
            if second is None or owner != second["PLAYER_ID"]:
                raise SystemExit("%s: город принадлежит %s, а должен — соседнему игроку"
                                 % (key, owner))
        elif owner != neutral["PLAYER_ID"]:
            raise SystemExit("%s: город принадлежит %s, а должен быть ЧУЖИМ (нейтральным)"
                             % (key, owner))
        if int(v["SIZE"]) != int(want["tier"]):
            raise SystemExit("%s: уровень города %s, ожидался %s"
                             % (key, v["SIZE"], want["tier"]))
        got = sorted(types[v["UNIT_%d" % k].upper()] for k in range(6)
                     if v["UNIT_%d" % k] != sg.NULL_ID)
        if got != sorted(u.lower() for u in want.get("garrison", ())):
            raise SystemExit("%s: гарнизон города %s, ожидался %s"
                             % (key, got, list(want.get("garrison", ()))))


def verify_sites(scen: sg.Sg, prof: dict, key: str) -> None:
    """Лавки: столько же, на месте, и каждая читается своим же курсором до последнего байта."""
    got = {}
    for r in scen.find("MidSite"):
        ms.parse_site(r.what, r.body)
        got[site_xy(r)] = r.what
    for s in prof.get("sites", ()):
        if got.get(tuple(s["xy"])) != ms.WHAT_TRAINER:
            raise SystemExit("%s: в клетке %s нет Тренера (%s) — лавки стоят в %s"
                             % (key, tuple(s["xy"]), s["title"], sorted(got)))
    if len(got) != len(prof.get("sites", ())):
        raise SystemExit("%s: лавок на карте %d, ожидалось %d"
                         % (key, len(got), len(prof.get("sites", ()))))


def verify(path: Path, key: str, t: sq.Tables, quiet: bool = False) -> None:
    """Прочитать записанный файл обратно и проверить числами всё, что генератор обещал."""
    prof = fp.profile(key)
    scen = sg.Sg.read(path)                  # чтение сверяет счётчик объектов и рамки записей
    check_header(scen, prof, key)
    human, neutral = players(scen)
    stacks = {r.obj_id: sg.parse_stack(r.body) for r in scen.find(sg.WHAT_STACK)}
    own = [s for s in stacks.values() if s["OWNER"] == human["PLAYER_ID"]]
    if len(own) != 1 + len(prof["parties"]):
        raise SystemExit("%s: своих отрядов %d, ожидался стартовый плюс %d поставленных "
                         "сценарием" % (key, len(own), len(prof["parties"])))
    foes = [s for s in stacks.values() if s["OWNER"] == neutral["PLAYER_ID"]]
    if len(foes) != len(prof["neutrals"]):
        raise SystemExit("%s: нейтральных отрядов %d, ожидалось %d"
                         % (key, len(foes), len(prof["neutrals"])))
    types = unit_types(scen)
    by_pos = {(int(s["POS_X"]), int(s["POS_Y"])): s for s in foes}
    for b in prof["neutrals"]:
        s = by_pos.get(b["xy"])
        if s is None:
            raise SystemExit("%s: в клетке %s нет нейтрального отряда" % (key, b["xy"]))
        got = types[s["LEADER_ID"].upper()]
        if got != b["leader"]:
            raise SystemExit("%s: нейтрал %s — лидер %s, ожидался %s"
                             % (key, b["xy"], got, b["leader"]))
    verify_parties(scen, prof, key, t)
    verify_bags(scen, prof, key)
    verify_spells(scen, prof, key)

    verify_second(scen, prof, key)     # раньше города: город M14 принадлежит именно соседу
    verify_town(scen, prof, key, neutral, types)
    verify_sites(scen, prof, key)
    check_plan_refs(scen, prof, key)
    if not quiet:
        print("  проверка чтения %s: потолки, имя в обоих местах, казна, грунт, нейтралы, "
              "%d отрядов игрока с ранами, %d мешков, %d заклинаний, город и план сошлись"
              % (key, len(prof["parties"]), len(prof["bags"]), len(prof["spells"])))


# ------------------------------------------------------------------ selftest


def second_race(scen: sg.Sg) -> str:
    """`RACE_ID` соседа: играбельный игрок, который не наш и не нейтрал."""
    return next(p["RACE_ID"] for p in sp.players_of(scen)
                if p["RACE_ID"].upper() not in (sc.RACE_EMPIRE.upper(),
                                                sc.RACE_NEUTRAL.upper()))


def sabotage_map(scen: sg.Sg, what: str, t: sq.Tables) -> sg.Sg:
    """Подсадная порча уже собранной карты — ровно одно обещание за раз."""
    if what == "maxunit":
        return city.set_info_int(scen, "MAX_UNIT", 2)
    if what == "leadermax":
        return city.set_info_int(scen, "MAX_LEADER", 1)
    if what == "bank":
        return set_bank(scen, players(scen)[0]["PLAYER_ID"],
                        "G0000:R0000:Y0000:E0000:W0000:B0000")
    if what == "novillage":
        return replace(scen, records=tuple(r for r in scen.records
                                           if r.what != city.WHAT_VILLAGE))
    if what == "name":
        return sg.set_scenario_name(scen, "чужое имя")
    if what == "noparty":                    # убрать отряд, поставленный сценарием
        drop = [r for r in own_stacks(scen)
                if sg.parse_stack(r.body)["INSIDE"] == sg.NULL_ID][:1]
        return replace(scen, records=tuple(r for r in scen.records if r not in drop))
    if what == "alive":                      # оживить павшего
        for r in scen.find(sg.WHAT_UNIT):
            if unit_hp(r) == 0:
                impl = str(sg.fields(r.body, sg.LAYOUT_UNIT)["TYPE"])
                return scen.with_record(r, replace(r, body=sg.unit_body(
                    r.obj_id, impl, int(t.unit(impl)["LEVEL"]),
                    int(t.unit(impl)["HIT_POINT"]))))
        raise SystemExit("нечего оживлять: в карте нет павшего юнита")
    if what == "leaderfull":                 # вылечить ЛИДЕРА поставленного отряда
        lid = next(sg.parse_stack(r.body)["LEADER_ID"].upper() for r in own_stacks(scen)
                   if sg.parse_stack(r.body)["INSIDE"] == sg.NULL_ID)
        u = next(r for r in scen.find(sg.WHAT_UNIT) if r.obj_id.upper() == lid)
        impl = str(sg.fields(u.body, sg.LAYOUT_UNIT)["TYPE"])
        return scen.with_record(u, replace(u, body=sg.unit_body(
            u.obj_id, impl, int(t.unit(impl)["LEVEL"]), int(t.unit(impl)["HIT_POINT"]))))
    if what == "tier":                       # сбить уровень города — M16 меряет ИМЕННО его
        rec = scen.find(city.WHAT_VILLAGE)[0]
        v = city.parse_village(rec.body, city._has_p_o_el(scen))
        return scen.with_record(rec, replace(rec, body=city.village_body(
            rec.obj_id, v["NAME_TXT"], v["OWNER"], v["SUBRACE"], int(v["POS_X"]),
            int(v["POS_Y"]), 1 if int(v["SIZE"]) != 1 else 2, [],
            has_p_o_el=city._has_p_o_el(scen))))
    if what == "nobag":
        drop = scen.find(WHAT_BAG)[:1]
        return replace(scen, records=tuple(r for r in scen.records if r not in drop))
    if what == "nospell":
        return set_known_spells(scen, players(scen)[0], [])
    if what == "nosite":                     # убрать лавку (Тренера)
        drop = scen.find("MidSite")[:1]
        return replace(scen, records=tuple(r for r in scen.records if r not in drop))
    if what == "nogarrison":                 # опустошить гарнизон города
        rec = scen.find(city.WHAT_VILLAGE)[0]
        v = city.parse_village(rec.body, city._has_p_o_el(scen))
        return scen.with_record(rec, replace(rec, body=city.village_body(
            rec.obj_id, v["NAME_TXT"], v["OWNER"], v["SUBRACE"],
            int(v["POS_X"]), int(v["POS_Y"]), int(v["SIZE"]), [],
            has_p_o_el=city._has_p_o_el(scen))))
    if what == "noroster":                   # раса соседа исчезла из ростера ScenarioInfo
        race = sp.race_number(second_race(scen))
        return sp.set_roster(scen, [sp.ROSTER_EMPTY if v == race else v
                                    for v in sp.roster(scen)])
    if what == "noheaderrace":               # раса соседа исчезла из таблицы рас заголовка
        race = sp.race_number(second_race(scen))
        return sp.set_header_races(scen, [v for v in sp.header_races(scen) if v != race])
    if what == "nocapstack":                 # гарнизон чужой столицы пропал
        pid = sp.second_player(scen, {"race": second_race(scen)})["PLAYER_ID"]
        rec = next(r for r in scen.find(sg.WHAT_CAPITAL)
                   if sg.fields(r.body, sg.LAYOUT_CAPITAL)["OWNER"] == pid)
        f = sg.fields(rec.body, sg.LAYOUT_CAPITAL)
        scen = scen.with_record(rec, replace(rec, body=sp.capital_body(
            rec.obj_id, str(f["NAME_TXT"]), str(f["OWNER"]), str(f["SUBRACE"]),
            (int(f["POS_X"]), int(f["POS_Y"])))))
        return replace(scen, records=tuple(r for r in scen.records
                                           if r.obj_id != f["STACK"]))
    if what == "nosecond":                   # снести соседнего игрока вместе со столицей
        gone = {r.obj_id for r in scen.find(sg.WHAT_PLAYER)
                if sg.fields(r.body, sg.LAYOUT_PLAYER)["RACE_ID"].upper()
                not in (sc.RACE_EMPIRE.upper(), sc.RACE_NEUTRAL.upper())}
        caps = {r.obj_id for r in scen.find(sg.WHAT_CAPITAL)
                if sg.fields(r.body, sg.LAYOUT_CAPITAL)["OWNER"] in gone}
        return replace(scen, records=tuple(r for r in scen.records
                                           if r.obj_id not in gone | caps))
    raise SystemExit("неизвестная порча карты %s" % what)


# Порча файла: (ключ порчи, подпись, поле профиля, без которого порча бессмысленна).
MAP_SABOTAGE = [("maxunit", "MAX_UNIT сбит на 2", None),
                ("leadermax", "потолок уровня героя 1", None),
                ("bank", "казна не та", None),
                ("name", "имя в списке игры не то", None),
                ("noparty", "отряд игрока пропал", "parties"),
                ("alive", "павший юнит оказался живым", "need_dead"),
                ("leaderfull", "раненый лидер оказался здоров", "leader_hp"),
                ("tier", "у города сбит уровень", "villages"),
                ("nobag", "мешок с вещью пропал", "bags"),
                ("nospell", "заклинание игроку не выдано", "spells"),
                ("novillage", "чужой город пропал", "village"),
                ("nosite", "Тренер пропал с карты", "sites"),
                ("nogarrison", "гарнизон города опустел", "garrison"),
                ("nosecond", "второй играбельный игрок пропал", "second"),
                # три порчи разбора падения 10.09.2026: именно так выглядела негодная запись
                ("noroster", "раса соседа не объявлена в ScenarioInfo", "second"),
                ("noheaderrace", "раса соседа не объявлена в заголовке", "second"),
                ("nocapstack", "у чужой столицы пустой гарнизон", "second")]


def applicable(prof: dict, needs: str | None) -> bool:
    """Порча применима только там, где соответствующее обещание вообще есть."""
    if needs is None:
        return True
    if needs == "garrison":
        return bool((prof.get("village") or {}).get("garrison"))
    if needs == "village":                   # и одиночное поле, и список M16
        return bool(fp.villages(prof))
    if needs == "leader_hp":
        return any(p.get("leader_hp") is not None for p in prof["parties"])
    return bool(prof.get(needs))


def map_cases(template: Path, scratch: Path, t: sq.Tables) -> list[tuple[str, str | None]]:
    """Сторожа файла: собираем честную карту, потом портим по одному обещанию.

    Порчи разложены по картам, где соответствующее обещание вообще есть: павший юнит только на
    M4, мешок только на M8, заклинание только на M6, чужой город только на M10.
    """
    scratch.mkdir(parents=True, exist_ok=True)
    out = scratch / "f7_selftest.sg"
    cases = []
    for key in fp.ORDER:
        prof = fp.profile(key)
        scen, _ = build(template, key, t)
        scen.write(out)
        honest = fp.raises(lambda: verify(out, key, t, quiet=True))
        if honest is not None:
            raise SystemExit("честная карта %s не проходит собственную проверку: %s"
                             % (key, honest))
        for what, title, needs in MAP_SABOTAGE:
            if not applicable(prof, needs):
                continue
            sabotage_map(scen, what, t).write(out)
            cases.append(("%s: %s" % (key, title), fp.raises(
                lambda: verify(out, key, t, quiet=True))))
    out.unlink()
    return cases


def live_cases(t: sq.Tables) -> list[tuple[str, str | None]]:
    """Проверка на ЖИВЫХ данных: сохранения первого хода прогона 09.09.2026, где заплата не
    сработала. Сторож обязан опознать тогдашний негодный состав — иначе он ловит не ту беду."""
    live = {"m4": "p1-f7-m4-temple-human-t01-015.sg",
            "m6": "p1-f7-m6-rods-human-t01-016.sg",
            "m8": "p1-f7-m8-mage-human-t01-065.sg"}
    cases = []
    for key, name in live.items():
        path = LIVE_RUN / name
        if not path.exists():
            raise SystemExit("нет живого сохранения %s — сторожа проверять не на чем" % path)
        scen, prof = sg.Sg.read(path), fp.profile(key)
        cases.append(("%s живьём: отряда нет" % key,
                      fp.raises(lambda s=scen, p=prof, k=key: verify_parties(s, p, k, t))))
        if prof["bags"]:
            cases.append(("%s живьём: мешков нет" % key,
                          fp.raises(lambda s=scen, p=prof, k=key: verify_bags(s, p, k))))
        if prof["spells"]:
            cases.append(("%s живьём: заклинания нет" % key,
                          fp.raises(lambda s=scen, p=prof, k=key: verify_spells(s, p, k))))
    return cases + live_capture_case(t)


def live_capture_case(t: sq.Tables) -> list[tuple[str, str | None]]:
    """Прежняя карта захвата M3 против сторожей M12 — на живом файле, а не на выдумке.

    Сторож простора на ней НЕ срабатывает, и это результат: свободной равнины вокруг города
    там было больше двух тысяч клеток, значит ходовая гипотеза «квоте некуда было тратиться»
    неверна. Срабатывает сторож ОТРЫВА: столица стояла в четырёх клетках от города, её
    собственное пятно успело дорасти до города, и разовая квота ушла на уже свои клетки.
    Оба факта проверяются здесь как пара — и «ловится», и «молчит».
    """
    path = LIVE_RUN / "p1-f7-m3-capture-human-t01-074.sg"
    if not path.exists():
        raise SystemExit("нет живого сохранения %s — сторожа проверять не на чем" % path)
    scen = sg.Sg.read(path)
    rec = scen.find(city.WHAT_VILLAGE)[0]
    like_m3 = dict(fp.profile("m12"),
                   village=dict(fp.profile("m12")["village"], xy=village_xy(rec)))
    room = fp.capture_room(like_m3)
    if room < int(fp.gvars()["T_CAPTURE"]):
        raise SystemExit("живая M3: простора вокруг города %d — разбор ниже опирался на "
                         "обратное, перечитать" % room)
    gap = min(fp.cheb(a, b) for a in c4.capital_tiles()
              for b in city.village_tiles(village_xy(rec)))
    return [("живая M3: отрыв %d по файлу, простора %d по модели" % (gap, room),
             fp.raises(lambda: fp.check_capture(t, like_m3, "живая M3")))]


def selftest(template: Path, scratch: Path) -> int:
    """Сторожа проверяются подсадной ошибкой: профили, файлы карт и живые сохранения."""
    t = sq.Tables()
    groups = [("профили", fp.profile_cases(t)),
              ("файлы карт", map_cases(template, scratch, t)),
              ("живые сохранения прогона 09.09.2026", live_cases(t))]
    bad = total = 0
    for title, cases in groups:
        if not cases:
            raise SystemExit("группа «%s» не сделала ни одного сравнения — это не проверка"
                             % title)
        print("--- %s: %d сравнений" % (title, len(cases)))
        for name, msg in cases:
            print("%-46s %s" % (name, ("ловится: " + msg.split("\n")[0]) if msg else "ПРОПУЩЕН"))
            bad += msg is None
        total += len(cases)
    print("selftest: сработали %d сторожей из %d сравнений" % (total - bad, total))
    return 1 if bad else 0


# ---------------------------------------------------------------------- main

def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--template", type=Path, default=c4.TEMPLATE)
    p.add_argument("--out-dir", type=Path, default=EXPORTS)
    p.add_argument("--only", choices=fp.ORDER, action="append",
                   help="собрать одну карту; можно повторять")
    p.add_argument("--plan", action="store_true", help="только расчёт, ничего не записывать")
    p.add_argument("--selftest", action="store_true", help="проверить сторожей подсадной ошибкой")
    p.add_argument("--scratch", type=Path,
                   default=Path(__file__).resolve().parents[1] / "corpus" / "_f7-selftest")
    a = p.parse_args(argv)
    if not a.template.exists():
        raise SystemExit("нет шаблона %s" % a.template)
    if a.selftest:
        return selftest(a.template, a.scratch)
    t = sq.Tables()
    fp.print_plan(t)
    if a.plan:
        return 0
    print()
    a.out_dir.mkdir(parents=True, exist_ok=True)
    for key in (a.only or fp.ORDER):
        scen, stats = build(a.template, key, t)
        out = a.out_dir / fp.profile(key)["file"]
        scen.write(out)
        print("%s: %s" % (out, ", ".join("%s %s" % kv for kv in stats.items())))
        verify(out, key, t)
    return 0


if __name__ == "__main__":
    sys.exit(main())
