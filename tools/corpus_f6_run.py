#!/usr/bin/env python3
r"""Один длинный прогон Ф6: карта, магия, объекты, постройка, найм и бои — за один запуск игры.

    python tools/corpus_f6_run.py --dry-run --route tools/f6_route.json \
        --from-file corpus/20260907-c4/map-001.jsonl --out corpus/_dry-f6
    python tools/corpus_f6_run.py --route tools/f6_route.json --calibrate \
        --save --out corpus/20260908-f6

**Прогон стартует с СОХРАНЕНИЯ, а не со сценария** (`--save`). Задать в файле сценария
построенную столицу или готовый отряд нельзя: старт сцены пересоздаёт игрока по выбранному
лорду и стирает и то, и другое (измерено 08.09.2026, см. `tools/patch_f6_save.py`). Порядок:
пользователь загружает сценарий, выбирает лорда, жмёт `Q`; `patch_f6_save.py` правит это
сохранение; прогон грузит его штатным путём `goto_map.py`. Без `--save` остаётся прежний путь
через сценарий `--scenario f6_route` — он годится только для проверок движения.

Надстройка над `corpus_c4_run.py`, а не его копия: манифест, перенос журнала, стартовая карта,
контрольные точки и правка `ConfirmEOT` берутся оттуда как есть. Ново три вещи.

1. **Действия на ходах.** У хода маршрута может быть список `actions` (схема — докстринг
   `tools/f6_route.py`): каст, посещение объекта, бой, ручной шаг мышью, сверка состава.
2. **Любая осечка — снимок, статус и ОСТАНОВКА.** Правило Ф5 распространено на действия:
   доигранное до обрыва годится для харнесса, сбитая последовательность — нет.
3. **Счётчики в конце.** По каждому типу события печатается «ожидалось / получено» из поля
   `expect` маршрута, и прогон возвращает ненулевой код, если хоть чего-то получено ноль.
   «Пустой лог ничего не доказывает» — главное правило репозитория, и здесь оно выражено
   числом, а не надеждой.

**Третий вид боя — `manual_battle`** (арена предметов, `tools/make_items_map.py`). Автобой
(`A`) там не годится: ИИ зелья не пьёт, а применения предмета у него нет вовсе. Драйвер
доводит отряд, печатает инструкцию, шагает на врага и ждёт `battle_end` до 600 с — человек
играет медленно. Сам шаг, сверки предметов по сохранению и счётчики следов применения живут
в `tools/corpus_items.py`: маршруты Ф6 и арены v2 их не видят и работают как раньше.

**Что делает пользователь.** Два шага — постройку и найм — драйвер не кликает: выбор здания на
схеме столицы и выбор юнита в окне найма мышью, хоткея нет (`docs/game-hotkeys.md`, ранг 1), а
координаты этих кликов ниоткуда не выводятся. Драйвер открывает нужное окно клавишей, печатает
инструкцию, ждёт, а затем **проверяет результат по сохранению**: постройка обязана появиться в
`CPlayerBuildings`, найм — в отряде столицы. Не появилась — снимок и остановка.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO_ROOT / "tools"))
import corpus_run as cr  # noqa: E402
import corpus_c4_run as c4run  # noqa: E402
import sgfile as sg  # noqa: E402
import make_f6_map as f6  # noqa: E402
import corpus_items as ci  # noqa: E402  (арена предметов: manual_battle, сверки, счётчики)
import patch_f6_save as pf6  # noqa: E402
import autoplay as ap  # noqa: E402  (tools/gamedrv, путь добавлен импортом corpus_run)
import goto_battle as gb  # noqa: E402
import goto_map as gm  # noqa: E402  (штатный путь «Одиночная игра» -> «Загрузить» -> автосохранение)
import ini  # noqa: E402
import map_route as mr  # noqa: E402

JOURNAL_NAME = "map-001.jsonl"
ACTIONS = ("verify_squad", "manual", "cast", "visit", "attack", "manual_battle")
KEY_SPELLBOOK = "S"          # карта: DLG_STRATEGIC BTN_CAST_SPELL (docs/game-hotkeys.md)
CAST_SETTLE = 1.0
BATTLE_SETTLE = 3.0
# Потолок ожидания `battle_end`. Было 90 с под бои Ф6 (три-пять раундов); арена Ф6 нарочно
# держит передний ряд врага живым и рассчитана на 4–7 раундов (`tools/make_arena_map.py`),
# поэтому потолок поднят. Это только срок ОТКАЗА: удачный бой кончается ровно тогда, когда
# приходит `battle_end`, и запас ничего не замедляет.
BATTLE_LIMIT = 240.0
MAP_WAIT = 8.0               # сколько ждать карту после действия, не нажимая ничего (анимация смены террейна)
MAX_ESCAPES = 4              # свитков подряд после боя у руины три: итоги, золото, предмет; запас один


# --------------------------------------------------------------- проверка маршрута

def validate(route: dict) -> None:
    """Разобрать все действия ДО запуска игры: осечка в схеме не должна стоить прогона."""
    if "expect" not in route:
        raise SystemExit("в маршруте нет поля expect — нечего сверять со счётчиками журнала")
    turns = {t["turn"] for t in route["turns"]}
    seen = Counter()
    for t in route["turns"]:
        for a in t.get("actions", []):
            do = a.get("do")
            if do not in ACTIONS:
                raise SystemExit("ход %d: неизвестное действие %r (есть %s)"
                                 % (t["turn"], do, ", ".join(ACTIONS)))
            if do in ("cast", "visit", "attack", "manual_battle"):
                target = a.get("target")
                stack_ok = do == "cast" and target == "stack"    # клетка отряда — из журнала
                if not stack_ok and not (isinstance(target, list) and len(target) == 2):
                    raise SystemExit("ход %d: у %s нет цели-клетки" % (t["turn"], do))
            if do == "cast":
                if not 1 <= int(a["page"]) <= 5:
                    raise SystemExit("ход %d: страница книги %s вне 1..5" % (t["turn"], a["page"]))
                if int(a["index"]) < 0:
                    raise SystemExit("ход %d: index %s отрицательный" % (t["turn"], a["index"]))
                if a["spell"] not in f6.KNOWN_SPELLS:
                    raise SystemExit("ход %d: заклинания %s игрок не знает — на карте его нет "
                                     "в CPlayerKnownSpells" % (t["turn"], a["spell"]))
            if do in ("manual", "manual_battle") and not a.get("text"):
                raise SystemExit("ход %d: ручной шаг без инструкции пользователю" % t["turn"])
            if do == "manual_battle" and a.get("verify", {}).get("kind", "battle_slots")                     not in ci.SAVE_CHECKS:
                raise SystemExit("ход %d: неизвестная сверка %r" % (t["turn"], a["verify"]))
            seen[do] += 1
    # Какие виды действий маршрут обязан содержать, он говорит сам. Умолчание — все четыре, как
    # у прогона Ф6; арена Ф6 (`tools/make_arena_map.py`) состоит из одних боёв и объявляет
    # `require_actions: ["attack", "verify_squad"]` — требовать от неё касты и найм значило бы
    # ронять верный маршрут.
    required = route.get("require_actions", ["cast", "visit", "attack", "manual"])
    missing = [n for n in required if not seen[n]]
    if missing:
        raise SystemExit("в маршруте нет ни одного действия: %s" % ", ".join(missing))
    print("маршрут разобран: ходов %d, действий %s"
          % (len(turns), " ".join("%s=%d" % kv for kv in sorted(seen.items()))))


# ------------------------------------------------------------- сверка по сохранению

def own_squad(scen: sg.Sg) -> list[str]:
    player = f6.human_player(scen)["PLAYER_ID"]
    types = {r.obj_id.upper(): sg.fields(r.body, sg.LAYOUT_UNIT)["TYPE"].lower()
             for r in scen.find(sg.WHAT_UNIT)}
    for r in scen.find(sg.WHAT_STACK):
        st = sg.parse_stack(r.body)
        if st["OWNER"] != player:
            continue
        return [types[st[f"UNIT_{k}"].upper()] for k in range(6)
                if st[f"UNIT_{k}"] != sg.NULL_ID]
    raise RuntimeError("в сохранении нет отряда игрока")


def capital_units(scen: sg.Sg) -> list[str]:
    import scenario_c4 as sc
    types = {r.obj_id.upper(): sg.fields(r.body, sg.LAYOUT_UNIT)["TYPE"].lower()
             for r in scen.find(sg.WHAT_UNIT)}
    city = sc.parse_city(scen.one(sg.WHAT_CAPITAL).body)
    return [types[city[f"UNIT_{k}"].upper()] for k in range(6)
            if city[f"UNIT_{k}"] != sg.NULL_ID]


def check_save(save: Path, kind: str, value: str) -> str:
    """Числовая проверка результата ручного шага. Возвращает строку отчёта или бросает."""
    scen = sg.Sg.read(save)
    if kind in ci.SAVE_CHECKS:      # арена предметов: боевые слоты и инвентарь лидера
        return ci.SAVE_CHECKS[kind](scen, value)
    if kind == "building":
        got = [b.upper() for b in f6.read_list(scen, f6.human_player(scen)["BUILDS_ID"],
                                               "BUILD_ID")]
        if value.upper() not in got:
            raise RuntimeError("постройки %s нет в CPlayerBuildings (есть %s)" % (value, got))
        return "постройки: %s" % got
    if kind == "hire":
        got = capital_units(scen)
        if value.lower() not in got:
            raise RuntimeError("юнита %s нет в отряде столицы (есть %s)" % (value, got))
        return "отряд столицы: %s" % got
    raise RuntimeError("неизвестная сверка %r" % kind)


# -------------------------------------------------------------------- действия

def wait_event(name: str, off: int, limit: float) -> list[dict]:
    deadline = time.time() + limit
    while time.time() < deadline:
        found = [e for e in mr.events_since(off) if e.get("ev") == name]
        if found:
            return found
        time.sleep(1.0)
    return []


def ensure_map() -> None:
    """Убедиться, что на экране карта. Escape — только пока журнал молчит, до MAX_ESCAPES раз.

    (Правило «не более одного Escape» было нужно при признаке по доле чёрного и снято 08.09.2026
    вместе с ним: свитки после боя у руины идут по три, а лишний Escape на карте при признаке
    по журналу невозможен.)

    Прежняя версия жала Escape до трёх раз, пока не опознает карту по доле тёмного. Это опасно:
    на карте лишний Escape открывает ОПЦИИ (правило репозитория, `docs/game-hotkeys.md`), а из
    опций недалеко до выхода из игры — 08.09.2026 прогон так и закончился, закрыв игру после
    захода в лавку, хотя лавка от одного Escape закрывается (пользователь проверил).

    Поэтому: если карты нет — один Escape и повторная проверка. Если и после него не карта,
    падаем со снимком, а не жмём вслепую дальше. Потерять прогон дешевле, чем закрыть игру.
    """
    # Сначала ЖДЁМ карту, и только потом жмём. Смена террейна (ход 44, 08.09.2026 дважды) играет
    # анимацию: кадр светлый, «не карта», Escape в этот момент падает на карту под анимацией и
    # открывает опции — их и увидела проверка (fail-turn-44.png). Свиток за это время никуда не
    # денется, а анимация — кончится; поэтому до Escape ждём до MAP_WAIT секунд без единого
    # нажатия, и только если карта так и не показалась — один Escape и ещё одно ожидание.
    # Свитки закрываются ПО ОДНОМУ, и каждый Escape заслужен молчанием журнала. После руины их
    # три подряд: итоги боя, золото, предмет (08.09.2026 13:40, ход 49 — драйвер давал два и
    # вставал с третьим на экране). Пока карта подтверждается журналом, лишний Escape на карте
    # невозможен: нажимаем только когда наведение молчит. Если Escape всё же открыл опции —
    # следующий круг их закроет тем же путём.
    if wait_map(MAP_WAIT):
        return
    for n in range(1, MAX_ESCAPES + 1):
        ap.key("escape")
        if wait_map(3.0):
            return
    raise RuntimeError("после действия на экране не карта: ждал %.0f с, затем %d раз Escape по 3 с "
                       "ожидания — журнал зонда не ответил ни на одно наведение (доля чёрного "
                       "%.1f%%, но она не признак)" % (MAP_WAIT, MAX_ESCAPES, gb.dark_share()))


def wait_map(seconds: float) -> bool:
    """Ждать карту, ничего не нажимая. Признак карты — ЖУРНАЛ, а не картинка.

    Прежний признак «чёрного >= 60 %» мерил не экран, а туман войны: на первых ходах он
    покрывает почти всё, к 44-му вокруг маршрута всё разведано, и карта дала 12,1 % (живой
    прогон 08.09.2026 13:05) — проверка сочла «не карта», нажала Escape и сама открыла опции.
    На карте наведение курсора на любую клетку рождает в журнале path / can_move / move_cost
    (тот же признак, на котором стоит калибровка); на свитке, в опциях, в бою — молчание.
    Точки наведения чередуются: зонд не пишет повтор по той же клетке (дедуп), а разные
    клетки — разные ключи.
    """
    # Порядок точек: где карта была в прошлый раз -> центр -> ВСЯ сетка. Первая правка брала
    # центр и сетку через одну (13 точек) и провалилась на первом же ходу (08.09.2026 13:15):
    # отряд в столице в углу карты, камера прижата к краю, все 13 точек — за краем; карту на
    # этой позиции калибровка тоже находит только сеткой, в (985,635). Полный проход по сетке
    # (54 точки, ~30 с) гарантирован хотя бы раз, прежде чем нажать что-либо: медленный отказ
    # дешевле ложного «не карта» с Escape в опции.
    points = list(dict.fromkeys(([LAST_MAP_POINT] if LAST_MAP_POINT else [])
                                + list(mr.ANCHORS) + mr.anchor_grid()))
    # Путь под курсором движок считает только для ВЫБРАННОГО отряда: после окна столицы выбор
    # снят, и наведение на карту молчит так же, как на свитке (08.09.2026 13:22, первый ход,
    # чёрного 12 % — это карта у освещённой столицы, а не окно). Калибровка перед наведениями
    # делает то же самое (select_stack: Tab, затем C — центр на отряде); вне карты эти клавиши
    # в диалогах ничего не значат (docs/game-hotkeys.md: в DLG_CAPITAL/DLG_OPTIONS их нет).
    mr.select_stack()
    deadline = time.time() + seconds
    i = 0
    while True:
        point = points[i % len(points)]
        if mr.hover(*point, settle=0.5) is not None:
            globals()["LAST_MAP_POINT"] = point
            gb.park_cursor()
            return True
        i += 1
        if i >= len(points) and time.time() >= deadline:
            return False


LAST_MAP_POINT: tuple[int, int] | None = None   # где журнал ответил в последний раз


def do_cast(a: dict, calib: dict) -> None:
    """Каст с карты: экранная точка цели считается ДО открытия книги.

    Пока книга открыта, наведение на карту не рисует путь, и опору взять не с чего — поэтому
    порядок именно такой: опора и точка, затем S, цифра страницы, index стрелок вниз, Enter,
    клик по заранее посчитанной точке (docs/game-hotkeys.md, «Заклинания на карте», ранг 1).
    """
    mr.select_stack()
    anchor = mr.find_anchor_stable(calib)
    target = a["target"]
    if target == "stack":
        # Клетка отряда СЕЙЧАС — из последнего stack_move журнала, а не из плана: после боя
        # отряд стоит там, куда шагнул к цели, и план этого места не знает (Исцеление после
        # руины, 08.09.2026).
        moves = [e for e in mr.events_since(0) if e.get("ev") == "stack_move"]
        if not moves:
            raise RuntimeError("каст %s на отряд: в журнале нет ни одного stack_move" % a["spell"])
        own = moves[0]["stack"]            # первым на ходу 1 из столицы выходит наш отряд
        e = [m for m in moves if m.get("stack") == own][-1]
        target = [int(e["x"]), int(e["y"])]
        print("    отряд %s по журналу стоит на %s" % (own, tuple(target)))
    sx, sy = mr.tile_to_screen(target[0], target[1], anchor, calib)
    ap.key(KEY_SPELLBOOK)
    time.sleep(CAST_SETTLE)
    ap.key(str(int(a["page"])))
    time.sleep(CAST_SETTLE)
    # Книга помнит не только страницу, но и выбранный пункт (08.09.2026: на странице 2 драйвер
    # нажал один `↓` и получил НУЛЕВОЙ пункт, Исцеление, вместо первого — значит курсор стоял не
    # на нуле). Поэтому не считаем от предполагаемого начала, а сначала уводим курсор в упор
    # вверх: пять `↑` при пяти местах на странице гарантируют нулевой пункт при любом исходном.
    for _ in range(5):
        ap.key("up")
        time.sleep(0.3)
    for _ in range(int(a["index"])):
        ap.key("down")
        time.sleep(0.5)
    ap.key("return")            # взять выбранное заклинание для применения — ОДИН Enter
    time.sleep(CAST_SETTLE)
    ap.click(sx, sy)            # цель указывается кликом по карте, одинаково для площадных
    time.sleep(2.0)
    # После каста — ТОЛЬКО ensure_map, никакого безусловного Escape. История одной строки:
    # 08.09.2026 утром Исцеление на полный отряд дало свиток «не возымеет эффекта», и я поставил
    # здесь Escape без условия. Днём он же уронил два прогона подряд на ходу 44: смена террейна
    # свитка не даёт, Escape ушёл в карту и открыл ОПЦИИ (снимок fail-turn-44.png). ensure_map
    # жмёт Escape только когда карты на экране нет — это и есть нужное условие.
    ensure_map()


def do_visit(a: dict, calib: dict, out_dir: Path) -> str:
    off = mr.mark()
    mr.play_turn(tuple(a["target"]), calib)
    # Заход подтверждается ОТКРЫВШИМСЯ ОКНОМ, а не событием `site`. Живой прогон 08.09.2026 11:56:
    # окно лавки открылось (пользователь видел), а `site` в журнале не появился ни разу — хук
    # `updateEncLayoutSite` пишет, на какой объект игрок СМОТРИТ (панель энциклопедии), заход в
    # лавку идёт в движке другим путём и его не трогает. Драйвер 15 с ждал события, которого
    # лавка не порождает, и ушёл в провал. Окно видно по кадру: карта ~84 % чёрного, экраны с
    # пергаментом 16–22 % (goto_battle.dark_share). ОГОВОРКА: «карта ~84 %» — это туман войны
    # первых ходов, к 44-му ходу карта даёт ~12 % (08.09.2026), и порог MAP_DARK_MIN карту от окна
    # УЖЕ НЕ отличает. Здесь он терпим только потому, что заходы стоят на ходах 2–4; для
    # поздних заходов признак нужен другой (например, молчание журнала на наведение — wait_map).
    deadline = time.time() + 15.0
    share = 100.0
    while time.time() < deadline:
        gb.park_cursor()
        share = gb.dark_share()
        if share < gb.MAP_DARK_MIN:
            break
        time.sleep(1.0)
    if share >= gb.MAP_DARK_MIN:
        raise RuntimeError("шаг на объект %s: окно не открылось за 15 с (на экране всё ещё карта, "
                           "чёрного %.1f%%) — объект не там или отряд до него не дошёл"
                           % (tuple(a["target"]), share))
    sites = [e for e in mr.events_since(off) if e.get("ev") == "site"]
    ap.key("escape")            # окно объекта закрывается ОДНИМ Escape (пользователь проверил)
    time.sleep(1.5)
    ensure_map()
    note = "окно объекта открылось (чёрного %.1f%%)" % share
    if sites:
        note += "; site: " + ", ".join(sorted({str(e.get("site")) for e in sites}))
    return note


def battle_open(off: int) -> bool:
    """Идёт ли бой прямо сейчас: с момента off начался и не кончился."""
    counts = Counter(e.get("ev") for e in mr.events_since(off))
    return counts.get("battle_start", 0) > counts.get("battle_end", 0)


def resolve_battle(off: int, why: str) -> str:
    """Доиграть бой автобоем игры и вернуться на карту.

    Клавиша `A` — штатный автобой (ранг 1, пользователь проверил 08.09.2026, docs/game-hotkeys.md),
    выход из оконченного боя — `Esc`. Форс-автобой ЗОНДА (маркер `probe/autobattle`) не
    используется: он подменяет ответ `isAutoBattle` хуком и ломает очередь ходов
    (память `probe-live-run-pitfalls`), а корпус ради очереди и снимается.
    """
    time.sleep(BATTLE_SETTLE)
    ap.key("A")
    ends = wait_event("battle_end", off, BATTLE_LIMIT)
    if not ends:
        raise RuntimeError("бой (%s) не кончился за %.0f с: battle_end в журнал не пришёл"
                           % (why, BATTLE_LIMIT))
    ap.key("escape")            # итоги боя -> карта
    time.sleep(2.0)
    ensure_map()
    counts = Counter(e.get("ev") for e in mr.events_since(off))
    return "бой (%s): победитель %s, " % (why, ends[-1].get("winner")) + " ".join(
        "%s=%d" % (k, counts.get(k, 0))
        for k in ("attack_new", "attack_get", "immune", "dmg", "xp"))


def guard_battle(off: int, where: str) -> str | None:
    """Незапланированный бой: заметить и доиграть, а не искать карту курсором.

    Живой прогон 08.09.2026: ход на (10,10) наткнулся на нейтральный отряд, начался бой
    (`battle_start`, `round`, 12 бросков, шесть `attack_new`), драйвер этого не заметил, пошёл
    кастовать заклинание, карты на экране не нашёл и впустую обошёл сетку из 54 точек. Маршрут
    знает только про СВОИ бои (боевой хвост, `f6.battle_turns`); чужие отряды по дороге — обычное дело, и встреча с
    ними не должна ронять прогон.
    """
    if not battle_open(off):
        return None
    print("  замечен незапланированный бой (%s) — доигрываю автобоем" % where)
    return resolve_battle(off, "незапланированный, %s" % where)


def do_attack(a: dict, calib: dict) -> str:
    """Шаг на чужой отряд — это бой."""
    off = mr.mark()
    mr.play_turn(tuple(a["target"]), calib, wait_ev="battle_start")
    return resolve_battle(off, "цель %s" % (tuple(a["target"]),))


def do_manual(a: dict, out_dir: Path, tag: str) -> str:
    """Шаг, у которого нет хоткея: окно открывает драйвер, мышью работает пользователь.

    Ожидание не слепое: сначала ждём событие, которое обязано родиться от самого окна
    (`expect_ev`), потом даём пользователю время, потом СВЕРЯЕМ РЕЗУЛЬТАТ ПО СОХРАНЕНИЮ.
    """
    off = mr.mark()
    for key in a.get("keys", []):
        ap.key(key)
        time.sleep(1.5)
    seconds = float(a.get("seconds", 60))
    print()
    print("  >>> РУЧНОЙ ШАГ (%.0f с): %s" % (seconds, a["text"]))
    print("  >>> драйвер ждёт и проверит результат по сохранению — не торопитесь")
    # Событие проверяем ПОСЛЕ работы пользователя, а не до неё. Живой прогон 08.09.2026: для
    # постройки `build_status` рождается от самого окна и приходил за секунды, а `hire_list`
    # рождается не от окна армии, а от КЛИКА по свободному слоту — то есть уже внутри отведённого
    # пользователю времени. Прежняя проверка ждала его 20 с сразу после клавиш и роняла прогон,
    # хотя событие потом приходило (итоговая таблица показывала «hire_list 1 — ок»).
    time.sleep(seconds)
    if a.get("expect_ev"):
        got = wait_event(a["expect_ev"], off, 5.0)
        if not got:
            raise RuntimeError("за %.0f с события %s так и не было — окно не открылось (клавиши %s "
                               "не дошли?) или шаг не выполнен"
                               % (seconds, a["expect_ev"], a.get("keys")))
        print("  событие %s пришло (%d шт.)" % (a["expect_ev"], len(got)))
    ensure_map()
    verify = a.get("verify")
    if not verify:
        return "ручной шаг без сверки"
    save = mr.quick_save(out_dir / ("probe-%s.sg" % tag))
    return check_save(save, verify["kind"], verify["id"])


def do_verify_squad(a: dict, out_dir: Path, start_map: Path) -> str:
    """Пережил ли отряд вход в игру. Ранг 3 снимается здесь, а не в конце прогона.

    Ответ получен живыми прогонами 08.09.2026, и он противоположен первой догадке:

    * **сценарий состав СОХРАНЯЕТ.** Прогон в 10:29 дал шесть юнитов —
      `g000uu0021` плюс все пять вынужденных, — хотя лидерство героя всего 4. То есть движок
      состав по лидерству при загрузке не режет;
    * **правленое сохранение состав ТЕРЯЕТ.** Прогон в 10:24: в пропатченном файле стек героя
      нёс 6 юнитов и 24 записи `UN`, после загрузки осталось 1 и 19 — игра удалила все пять
      дописанных записей целиком (не четыре из пяти, как было бы при урезании по лидерству).

    Первая догадка «сценарий стирает солдат» родилась из скриншота «Лидерство 0/3», который на
    деле был снят с чистого `QuickSave` сразу после старта, когда отряда ещё не было ни в каком
    виде. Отсюда правило: отряд задаём СЦЕНАРИЕМ, дописывать юнитов в сохранение бесполезно.

    Проверка остаётся: если состав всё же урезан, дальше ехать бессмысленно — пять классов атак
    держатся ровно на этих юнитах, и заменить их в ванили нечем.
    """
    save = mr.quick_save(out_dir / "probe-squad.sg")
    got = own_squad(sg.Sg.read(save))
    want = own_squad(sg.Sg.read(start_map))
    missing = [u for u in want if u not in got]
    if missing:
        raise RuntimeError("после входа в игру в отряде нет %s (есть %s, ожидалось %s) — "
                           "лидерство героя урезало состав" % (missing, got, want))
    return "состав отряда: %s" % got


def run_actions(actions: list[dict], when: str, ctx: dict) -> None:
    for n, a in enumerate(actions):
        if a.get("when", "after") != when:
            continue
        tag = "%02d-%d" % (ctx["turn"], n)
        print("  действие %s %s" % (a["do"], a.get("spell") or a.get("kind") or ""))
        # Перед каждым действием: не идёт ли бой, начатый предыдущим шагом. Свои бои маршрут
        # знает (боевой хвост, `f6.battle_turns`), чужие отряды по дороге — нет, и встреча с ними не должна ронять
        # прогон (08.09.2026: ход на (10,10) наткнулся на нейтрала, драйвер пошёл кастовать и
        # искал карту курсором, которой на экране не было).
        if a["do"] not in ("attack", "manual_battle"):
            fought = guard_battle(ctx["turn_off"], "перед %s" % a["do"])
            if fought:
                print("    %s" % fought)
                ctx["done"].append({"turn": ctx["turn"], "do": "battle", "note": fought})
        if a["do"] == "cast":
            do_cast(a, ctx["calib"])
            note = "каст %s (страница %s, index %s)" % (a["spell"], a["page"], a["index"])
        elif a["do"] == "visit":
            note = do_visit(a, ctx["calib"], ctx["out"])
        elif a["do"] == "attack":
            note = do_attack(a, ctx["calib"])
        elif a["do"] == "manual":
            note = do_manual(a, ctx["out"], tag)
        elif a["do"] == "manual_battle":
            note = ci.manual_battle(a, ctx["calib"], ctx["out"], tag)
        elif a["do"] == "verify_squad":
            note = do_verify_squad(a, ctx["out"], ctx["start_map"])
        else:
            raise RuntimeError("неизвестное действие %r" % a["do"])
        print("    %s" % note)
        ctx["done"].append({"turn": ctx["turn"], "do": a["do"], "note": note})


# ---------------------------------------------------------------------- прогон

def play_route(route: dict, out_dir: Path, scenario: str, calibrate: bool, state: dict,
               from_save: bool = False, lord: str = "mage") -> None:
    saves: list[dict] = state["saves"]
    if from_save:
        # Загрузка патченого сохранения: лорд уже выбран пользователем, игрок уже создан —
        # именно поэтому отряд, казна, заклинания и постройки переживают вход в игру.
        gm.open_to_map()
    else:
        # Лорд задаёт СТАРТОВОГО ГЕРОЯ, и это не косметика: «Маг» даёт Архимага (REACH 1,
        # площадная атака — достаёт задний ряд врага и убивает носителя редкого класса первым
        # же ходом, что и случилось в прогоне Ф6), «Полководец» — пешего Pegasus Knight
        # (REACH 3, только передний ряд). Арене нужен именно второй.
        gb.open_to_map(scenario=scenario, lord=lord)
    if calibrate:
        mr.calibrate()
    calib = mr.load_calib()
    checkpoints = set(route["checkpoints"])
    ctx = {"calib": calib, "out": out_dir, "done": state["done"],
           "start_map": out_dir / "map-start.sg", "turn": 0}

    for item in route["turns"]:
        turn, target = item["turn"], tuple(item["target"])
        ctx["turn"] = turn
        ctx["turn_off"] = mr.mark()      # от него сторож видит бой, начатый шагом этого хода
        actions = item.get("actions", [])
        run_actions(actions, "before", ctx)
        got = mr.play_turn(target, calib)
        print("ход %2d: цель %s, стал %s%s"
              % (turn, target, got, "" if got == target else "  (не дошёл — бюджет хода)"))
        fought = guard_battle(ctx["turn_off"], "после шага хода %d" % turn)
        if fought:
            print("  %s" % fought)
            ctx["done"].append({"turn": turn, "do": "battle", "note": fought})
        run_actions(actions, "after", ctx)
        day = mr.end_turn()
        state["turns"] = turn
        if day is not None:
            print("   день по журналу: %d" % day)
        if turn in checkpoints:
            saves.append(c4run.save_row(turn, mr.quick_save(out_dir / ("save-%02d.sg" % turn))))
    saves.append(c4run.save_row("final", mr.quick_save(out_dir / "save-final.sg")))


def report_counts(route: dict, counts: Counter) -> bool:
    """«Ожидалось / получено» по каждому типу события. Ноль полученных — провал, а не пропуск."""
    print()
    print("%-14s %10s %10s" % ("событие", "ожидалось", "получено"))
    ok = True
    for name, want in sorted(route.get("expect", {}).items()):
        got = counts.get(name, 0)
        mark = "ок" if got else "НЕТ НИ ОДНОГО"
        if not got:
            ok = False
        print("%-14s %10d %10d  %s" % (name, want, got, mark))
    extra = sorted(set(counts) - set(route.get("expect", {})))
    if extra:
        print("прочие события: " + " ".join("%s=%d" % (k, counts[k]) for k in extra))
    return ok


ATTACK_CLASS = {1: "Damage", 2: "Drain", 3: "Paralyze", 6: "Heal", 7: "Fear", 8: "BoostDamage",
                9: "Petrify", 10: "LowerDamage", 11: "LowerInitiative", 12: "Poison",
                13: "Frostbite", 14: "Revive", 15: "DrainOverflow", 16: "Cure", 17: "Summon",
                18: "DrainLevel", 19: "GiveAttack", 20: "Doppelganger", 21: "TransformSelf",
                22: "TransformOther", 23: "Blister", 24: "BestowWards", 25: "Shatter"}


def count_classes(journal: Path) -> Counter:
    """Сколько раз каждый КЛАСС атаки появился в журнале.

    Событие `ev` тут ничего не решает: все семнадцать классов сидят внутри одного `attack_new`,
    и без разбора по классу счётчик «attack_new = 300» не отвечает на вопрос фазы «сходил ли
    носитель». Класс берётся из `disp_class` (на чём движок диспетчеризовался), а если его нет
    — из `class` самой атаки; `notComputed` отрицателен и потому отсеивается сравнением с нулём.
    """
    counts: Counter = Counter()
    with journal.open("r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if '"attack_new"' not in line:
                continue
            try:
                e = json.loads(line)
            except ValueError:
                continue
            if e.get("ev") != "attack_new":
                continue
            for key in ("disp_class", "class"):
                v = e.get(key)
                if isinstance(v, int) and v > 0:
                    counts[v] += 1
                    break
    return counts


def report_classes(route: dict, journal: Path | None) -> bool:
    """«Ожидалось / получено» по классам атак. Ноль полученных — провал, и он назван поимённо."""
    want = route.get("expect_class")
    if not want:
        return True
    if journal is None or not journal.exists():
        print("журнала нет — классы атак сверять не с чем")
        return False
    counts = count_classes(journal)
    print()
    print("%-16s %10s %10s" % ("класс атаки", "ожидалось", "получено"))
    ok = True
    for cid, need in sorted(want.items(), key=lambda kv: int(kv[0])):
        got = counts.get(int(cid), 0)
        mark = "ок" if got >= int(need) else ("МАЛО" if got else "НЕТ НИ ОДНОГО")
        if got < int(need):
            ok = False
        print("%-16s %10d %10d  %s" % (ATTACK_CLASS.get(int(cid), cid), int(need), got, mark))
    extra = sorted(set(counts) - {int(c) for c in want})
    if extra:
        print("прочие классы: " + " ".join("%s=%d" % (ATTACK_CLASS.get(c, c), counts[c])
                                           for c in extra))
    return ok


def report_ai_probe(route: dict, journal: Path | None) -> None:
    """Проверка идеи «стравить два ИИ»: шевелится ли чужой отряд и дерётся ли он сам.

    Наши бои маршрут знает поимённо, поэтому лишний `battle_start` и `stack_move` не нашего
    отряда — это и есть ответ. Ни то ни другое провалом не считается: гипотеза проверяется, а
    не выполняется (разбор — в отчёте арены).
    """
    probe = (route.get("arena") or {}).get("ai_probe")
    if not probe or journal is None or not journal.exists():
        return
    own = str(route.get("stack", "")).upper()
    moved: Counter = Counter()
    ended = 0
    with journal.open("r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if '"stack_move"' not in line and '"battle_end"' not in line:
                continue
            try:
                e = json.loads(line)
            except ValueError:
                continue
            if e.get("ev") == "battle_end":
                ended += 1
            elif e.get("ev") == "stack_move":
                sid = str(e.get("stack", "")).upper()
                if sid and sid != own:
                    moved[sid] += 1
    ours = int((route.get("expect") or {}).get("battle_end", 0))
    print()
    print("проверка «ИИ против ИИ» (пара отрядов %s с приказом L_ATTACK_STACK друг на друга):"
          % probe)
    print("  шагов чужих отрядов: %d %s"
          % (sum(moved.values()), dict(moved) if moved else "— ни один чужой отряд не двинулся"))
    # Бой режется по battle_end (battle_start пишется перед каждым раундом, fidelity-f6 §4.5).
    print("  боёв в журнале %d, наших по маршруту %d -> %s"
          % (ended, ours, "есть бой без нашего участия" if ended > ours else "чужих боёв нет"))


def archive_if_failed(out_dir: Path) -> bool:
    """Увести каталог в архив, если лежащий в нём прогон неудачный. True — увели.

    Осечка на живом прогоне — дело обычное (08.09.2026 их было две подряд: недоступное здание,
    затем опора калибровки за краем карты), и каждый раз каталог оказывался занят, а пользователь
    получал отказ вместо запуска. Удачный прогон не трогаем никогда: корпус вне git, затереть
    его по невнимательности нельзя. Имя — правило пользователя: старые прогоны в каталогах с "_".
    """
    manifest = out_dir / "manifest.json"
    try:
        rows = json.loads(manifest.read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        print("манифест %s не читается (%s) — считаю прогон неудачным" % (manifest, e))
        rows = []
    if any(str(r.get("status", "")) == "ok" for r in rows):
        return False
    status = rows[0].get("status", "нет статуса") if rows else "пустой манифест"
    dest = out_dir.with_name("_failed-%s-%s" % (out_dir.name, datetime.now().strftime("%H%M%S")))
    out_dir.rename(dest)
    print("прогон в %s неудачный (%s) — уведён в %s" % (out_dir.name, status, dest.name))
    return True


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--route", type=Path, required=True, help="tools/f6_route.json")
    p.add_argument("--out", type=Path, default=None)
    p.add_argument("--scenario", default="f6_route")
    p.add_argument("--calibrate", action="store_true")
    p.add_argument("--resume", action="store_true")
    p.add_argument("--dry-run", action="store_true",
                   help="без игры: разобрать маршрут и посчитать события в журнале --from-file")
    p.add_argument("--from-file", type=Path, default=None)
    p.add_argument("--lord", choices=sorted(gb.LORD_ICON), default=None,
                   help="тип лорда = стартовый герой; без ключа берётся поле lord маршрута, "
                        "а без него — mage (Архимаг, как в прогоне Ф6)")
    p.add_argument("--save", type=Path, nargs="?", const=pf6.AUTOSAVE, default=None,
                   help="грузить патченое СОХРАНЕНИЕ, а не сценарий (default пути: "
                        "SaveGame/Автосохранение.sg — его готовит tools/patch_f6_save.py)")
    args = p.parse_args()

    if args.dry_run and args.from_file is None:
        p.error("--dry-run требует --from-file")
    if args.from_file is not None and not args.from_file.exists():
        p.error("нет файла %s" % args.from_file)
    if args.save is not None and not args.save.exists() and not args.dry_run:
        p.error("нет сохранения %s — сначала python tools/patch_f6_save.py --save ..." % args.save)

    route = c4run.load_route(args.route)         # проверка ходов и контрольных точек — от Ф5
    validate(route)                              # проверка действий — своя
    out_dir = args.out or (REPO_ROOT / "corpus" / (datetime.now().strftime("%Y%m%d") + "-f6"))
    out_dir.mkdir(parents=True, exist_ok=True)
    if (out_dir / "manifest.json").exists() and not args.resume:
        # Неудавшийся прогон каталог не занимает: уводим его в архив сам, а не заставляем
        # пользователя чистить руками. Удачный — только вручную, чтобы корпус нельзя было
        # затереть по невнимательности. Имя архива — правило пользователя: каталоги с "_".
        if archive_if_failed(out_dir):
            out_dir.mkdir(parents=True, exist_ok=True)
        else:
            p.error("%s/manifest.json уже существует и прогон в нём УДАЧНЫЙ — передайте --resume "
                    "или другой --out; затирать корпус автоматически нельзя" % out_dir)

    print("маршрут %s: ходов %d, контрольные точки %s, старт %s"
          % (args.route.name, len(route["turns"]), route["checkpoints"],
             ("сохранение %s" % args.save.name) if args.save else ("сценарий %s" % args.scenario)))
    inputs = c4run.stash_inputs(out_dir, args.route, args.scenario)
    if args.save is not None and args.save.exists():
        # Стартовое состояние прогона — патченое сохранение, а не файл сценария: именно с ним
        # сверяет состав `verify_squad` и от него считает харнесс С4.
        import shutil as _sh
        _sh.copy2(args.save, out_dir / "map-start.sg")
        inputs = [c4run.save_row("start", out_dir / "map-start.sg") if r["turn"] == "start" else r
                  for r in inputs]

    started_at = datetime.now(timezone.utc).isoformat()
    t0 = time.time()
    dest: Path | None = None
    status = "ok"
    state: dict = {"turns": 0, "saves": [], "done": []}

    if args.dry_run:
        import shutil
        dest = out_dir / JOURNAL_NAME
        shutil.copy2(args.from_file, dest)
        print("--dry-run: журнал %s скопирован как %s, игра не запускалась"
              % (args.from_file.name, dest.name))
    else:
        probe_dir = ap.GAME / "probe"
        ap.kill_leftover_game()
        before = set(probe_dir.glob("*.jsonl")) if probe_dir.is_dir() else set()
        eot_before = ini.get("Settings", "ConfirmEOT")
        if eot_before is not None and eot_before.strip() != "0":
            ini.set("Settings", "ConfirmEOT", "0")
            print("Disciple.ini: ConfirmEOT %s -> 0 (вернём после прогона)" % eot_before.strip())
        try:
            play_route(route, out_dir, args.scenario, args.calibrate, state,
                       from_save=args.save is not None,
                       lord=args.lord or route.get("lord", "mage"))
        except Exception as e:
            status = "route_failed"
            print("СБОЙ на ходу %d: %s" % (state["turns"] + 1, e))
            try:
                ap.screenshot(out_dir / ("fail-turn-%02d.png" % (state["turns"] + 1)))
            except Exception as shot_error:
                print("  снимок при сбое не удался: %s" % shot_error)
        finally:
            try:
                ap.kill_leftover_game()
                gb.use_scenario(None)            # вернуть оригинал в Exports/test_fast_battle.sg
            finally:                             # ini вернуть даже при сбое выше
                if eot_before is not None and eot_before.strip() != "0":
                    ini.set("Settings", "ConfirmEOT", eot_before.strip())
                    print("Disciple.ini: ConfirmEOT возвращён в %s" % eot_before.strip())
        fresh = [q for q in probe_dir.glob("*.jsonl") if q not in before]
        if fresh:
            candidate = out_dir / JOURNAL_NAME
            journal = max(fresh, key=lambda q: q.stat().st_mtime)
            if cr.move_journal(journal, candidate):
                dest = candidate
            else:
                status = "move_failed"
                print("перенос журнала не удался, остаётся в probe/: %s" % journal)
        else:
            status = status if status != "ok" else "no_journal"
            print("новых журналов в %s не появилось — зонд не установлен?" % probe_dir)

    rows, counts = c4run.build_manifest(out_dir, dest, args.route, route, args.scenario,
                                        started_at, time.time() - t0, status, state["turns"],
                                        state["saves"], inputs)
    row = rows[0]
    row["actions_done"] = state["done"]
    cr.write_json_atomic(out_dir / "manifest.json", rows)
    print()
    print("статус %s, ходов сыграно %d из %d, действий выполнено %d, сохранений %d, событий %d"
          % (row["status"], row["turns_played"], len(route["turns"]), len(state["done"]),
             len(row["saves"]), row["events"]))
    counts_ok = report_counts(route, counts)
    classes_ok = report_classes(route, dest)
    items_ok = ci.report_items(route, dest)
    report_ai_probe(route, dest)
    cr.update_root_manifest(REPO_ROOT / "corpus")
    return 0 if row["status"] == "ok" and counts_ok and classes_ok and items_ok else 1


if __name__ == "__main__":
    sys.exit(main())
