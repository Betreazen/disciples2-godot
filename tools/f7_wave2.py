#!/usr/bin/env python3
"""Числа и сторожа Ф7, не зависящие от списка карт: геометрия, чтение `.dbf`, и всё, что
проверяет вторая волна карт M11…M14 (обучение, простор под захват, статистика вора, сосед).

Отдельный файл, потому что `f7_plan.py` перевалил бы за восемьсот строк. Зависимость строго
односторонняя: `f7_plan` импортирует отсюда, обратно — никогда, иначе кольцо импортов.

Игра отсюда не запускается: модуль только считает и роняет сборку.
"""
from __future__ import annotations

import functools
import math
import sys
from collections import deque
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding="utf-8")
import make_c4_map as c4  # noqa: E402
import make_city_map as city  # noqa: E402  (village_tiles — пятно города 4x4)
import scenario_squads as sq  # noqa: E402
from dbf_dump import read as dbf_read  # noqa: E402


# --------------------------------------------------------------------- герои

HERO_FIGHTER = "g000uu0019"   # Pegasus Knight, LEADER_CAT 1, лидерство 4
HERO_MAGE = "g000uu0021"      # Archmage,       LEADER_CAT 3, L_ORB_USE врождённый (GLabi 14)
HERO_ROD = "g000uu0022"       # Arch-Angel,     LEADER_CAT 4, L_ROD (GLabi 13) — он ставит жезлы
HERO_THIEF = "g000uu0023"     # Thief,          LEADER_CAT 5 (L_NOBLE — это ВОР, не дворянин)
CAT_FIGHTER, CAT_MAGE, CAT_ROD, CAT_THIEF = 1, 3, 4, 5


# ------------------------------------------------------------------ геометрия

CAP = c4.CAPITAL_XY                       # (5,5); пятно 5x5 занимает 5..9 по обеим осям
NEAR_CAPITAL = (10, 10)                   # по диагонали от угла столицы (9,9): один шаг внутрь
ROD_MIN_GAP = 3                           # Чебышёв между жезлами (0x5D9BDB, коробка 5x5)
ROD_CITY_GAP = 2                          # не ближе клетки к городу (0x6137FE, коробка 3x3)
PLOT_MIN_GAP = 12                         # между площадками: суточные приросты не должны слиться
NEUTRAL_MIN_GAP = 3                       # между нейтралами: иначе бой начнётся вне очереди
HP_MARGIN = 0.15                          # запас HP переднего ряда сверх расчётных потерь

MAP_SIZE = 48                             # размер шаблона test_fast_battle.sg
PLAIN_COST = 2                            # цена шага по равнине без дороги (0x6027f3)
MOVE_RESERVE = 0.7                        # какую долю очков хода разрешено съесть кругу до лавки
SITE_TILES = 3                            # пятно лавки 3x3; вход — правый нижний угол (0x600d63)


def cheb(a: tuple[int, int], b: tuple[int, int]) -> int:
    return max(abs(a[0] - b[0]), abs(a[1] - b[1]))


def capital_gap(cell: tuple[int, int]) -> int:
    return min(cheb(cell, t) for t in c4.capital_tiles())


def capital_tiles_at(xy: tuple[int, int]) -> list[tuple[int, int]]:
    """Пятно столицы 5x5 в произвольном месте — своя столица стоит не одна только на M14."""
    x0, y0 = xy
    return [(x0 + dx, y0 + dy)
            for dy in range(c4.CAPITAL_SIZE) for dx in range(c4.CAPITAL_SIZE)]


def site_tiles(xy: tuple[int, int]) -> list[tuple[int, int]]:
    x0, y0 = xy
    return [(x0 + dx, y0 + dy) for dy in range(SITE_TILES) for dx in range(SITE_TILES)]


def site_entrance(xy: tuple[int, int]) -> tuple[int, int]:
    """`CMidSite::getEntrancePoint` 0x600d63: `(POS_X + sizeX − 1, POS_Y + sizeY − 1)` —
    правый нижний угол пятна. Поля «сторона входа» в записи нет, развернуть нечем."""
    return xy[0] + SITE_TILES - 1, xy[1] + SITE_TILES - 1


def plain_tiles(size: int, prof: dict | None = None) -> dict[tuple[int, int], bytes]:
    """Вся карта — равнина: жезл ставится только на равнину, а стоимость шага здесь не мерят.

    `prof` дописывает поверх равнины пятно чужой столицы (её террейн — расы соседа, а не
    Империи, иначе счёт «своей земли» по `tile & 7 == 1` сломается) и подсадные горы
    `walls`, которыми `--selftest` доказывает, что сторож простора не молчит.
    """
    tiles = {(x, y): c4.TILE[0] for y in range(size) for x in range(size)}
    for x, y in c4.capital_tiles():
        tiles[(x, y)] = c4.TILE_CAPITAL
    second = (prof or {}).get("second")
    if second:
        for x, y in capital_tiles_at(second["capital"]):
            tiles[(x, y)] = bytes([second["terrain"], 0, 0, 0])
    for x, y in (prof or {}).get("walls", ()):
        tiles[(x, y)] = c4.TILE[4]
    return tiles


def passable(tile: bytes) -> bool:
    """Грунт клетки — старшие биты байта: 0 равнина, 1 лес проходимы; 3 вода, 4 горы нет."""
    return (tile[0] >> 3) in (0, 1)


# ------------------------------------------------------ числа из таблиц, а не из головы

@functools.lru_cache(maxsize=None)
def gvars() -> dict:
    return dbf_read(sq.GLOBALS / "GVars.dbf")["rows"][0]


@functools.lru_cache(maxsize=None)
def grace(race_id: str) -> dict:
    for row in dbf_read(sq.GLOBALS / "Grace.dbf")["rows"]:
        if row["RACE_ID"].lower() == race_id.lower():
            return row
    raise SystemExit("расы %s нет в Grace.dbf" % race_id)


@functools.lru_cache(maxsize=None)
def action_chance(category: int) -> int:
    """`Gaction.CHANCE` действия вора по категории `Laction` — база до вычета сопротивления."""
    for row in dbf_read(sq.GLOBALS / "Gaction.dbf")["rows"]:
        if int(row["CATEGORY"]) == category:
            return int(row["CHANCE"])
    raise SystemExit("действия вора категории %d нет в Gaction.dbf" % category)


def bank_gold(bank: str) -> int:
    """Золото из строки банка `G0005:R0000:…` — и из `BANK`, и из `INCOME`, и из `TRAINING_C`."""
    return int(bank.split(":")[0][1:])


RACE_EMPIRE = "g000rr0000"
INCOME_GOLD = bank_gold(grace(RACE_EMPIRE)["INCOME"])   # 100 в день, тик идёт уже при загрузке


# ------------------------------------------------- данные второй волны (M11…M14)

GOBLIN = "g000uu5017"          # 50 HP, ARMOR 0: строка «Защита» покажет ЧИСТУЮ прибавку города
SQUIRE = "g000uu0001"          # Squire: XP_NEXT 80, TRAINING_C 4 -> полное обучение 316
ARCHER = "g000uu0006"          # Archer: XP_NEXT 70, TRAINING_C 4 -> полное обучение 276
KNIGHT = "g000uu0002"          # Knight: уровень 2, XP_NEXT 475, TRAINING_C **5** — ДРУГАЯ
                               # ставка. У Squire и Archer она одинаковая (4), поэтому «ставка
                               # из Gunits» и «всегда 4» на прежнем отряде неотличимы
TRAINER_LSITE = 6             # Lsite.dbf: L_TRAINER. Строки 3 в ванили помечены удалёнными
TRAINER_XY = (11, 3)           # пятно 11..13 x 3..5, вход (13,5) — раскладка карты `sites`

CAPTURE_TIER = 3               # уровень нейтрального города: PROT_3 = 20 к броне защитникам
CAPTURE_CLEAN_GAP = 10         # столица не ближе: её пятно за 20 дней вырастет с 25 до 225
                               # клеток (T_CAPITAL 10/сутки), это радиус 5 — 10 даёт запас вдвое

THIEF_WEAK = "g000uu6004"      # NEGOTIATE 10  -> показанный шанс 90 - 10 = 80 %
THIEF_HARD = "g000uu5119"      # Красный дракон, NEGOTIATE 70 -> 90 - 70 = 20 %
CAT_MISFIT, CAT_POISON_STACK = 4, 0   # Laction: L_MISFIT и L_POISON_STACK, у обоих CHANCE 90
THIEF_ACTS_PER_TURN = 1        # ДОПУЩЕНИЕ вслух: как снос жезла, действие съедает остаток хода

SECOND_RACE = "g000rr0001"     # Горные кланы: другая раса, значит шкала отношений существует
SECOND_LORD = "g000LR0004"
SECOND_SUBRACE = 4             # Lsubrace L_DWARF
SECOND_TERRAIN = 2             # Lterrain L_DW — под чужой столицей лежит ЕЁ грунт
SECOND_CAPITAL = (42, 42)      # дальний угол: 33 клетки от нашей столицы
RELATION_START = 75            # мир (> GVars.D_NEUTRAL 66); после -15 и двух -5 останется 50


def villages(prof: dict) -> list[dict]:
    """Города профиля. Восемь карт задают ОДИН город полем `village`, M16 — пять полем
    `villages`; всё, что обходит города, ходит через этот список, а не через поле."""
    one = prof.get("village")
    return ([one] if one else []) + list(prof.get("villages", ()))


def occupied_cells(prof: dict) -> list[tuple[tuple[int, int], str]]:
    """Клетки, занятые объектами профиля, с подписью — для проверки на наложение и на план."""
    out = [(tuple(n["xy"]), "нейтрал") for n in prof["neutrals"]]
    out += [(tuple(p["xy"]), "отряд игрока") for p in prof["parties"]]
    out += [(tuple(b["xy"]), "мешок") for b in prof["bags"]]
    out += [(xy, "лавка") for s in prof.get("sites", ()) for xy in site_tiles(s["xy"])]
    for v in villages(prof):
        out += [(xy, "город") for xy in city.village_tiles(v["xy"])]
    if prof.get("second"):
        out += [(xy, "чужая столица") for xy in capital_tiles_at(prof["second"]["capital"])]
    return out


# ------------------------------------------------ M11: расписание обучения (S4)

def training_price(t: sq.Tables, unit_id: str, xp: int = 0) -> tuple[int, int]:
    """(цена очка `Gunits.TRAINING_C`, сколько очков осталось до потолка `XP_NEXT − 1`).

    Тренер никогда не поднимает уровень: потолок на единицу ниже порога (`0x5e0396`, ранг 1,
    подтверждено пятью обучениями первого прогона — 576 / 170 / 3495 сошлись точно).
    """
    row = t.unit(unit_id)
    return bank_gold(str(row["TRAINING_C"])), int(row["XP_NEXT"]) - 1 - xp


def training_schedule(t: sq.Tables, prof: dict) -> list[dict]:
    """Прогон казны по дням: когда какого юнита учить и что при этом спишется.

    Частичное обучение читается как частичное лечение (`0x5DE1C1`): цена вычитается по разу
    на очко, значит куплено = ⌊казна / TRAINING_C⌋, зажатое потолком. Доход `Grace.INCOME`
    прибавляется КАЖДЫЙ день, включая нулевой — тик идёт уже при загрузке карты.
    """
    gold, xp, day, rows = bank_gold(prof["bank"]), {}, 0, []
    for unit_id, mode in prof.get("train", ()):
        price, need = training_price(t, unit_id, xp.get(unit_id, 0))
        while True:
            day, gold = day + 1, gold + INCOME_GOLD
            if mode == "partial" or gold >= price * need or day > prof["turns"]:
                break
        points = max(min(gold // price, need), 0)
        gold -= points * price
        xp[unit_id] = xp.get(unit_id, 0) + points
        rows.append({"день": day, "юнит": unit_id, "режим": mode, "очков": points,
                     "из": need, "списано": points * price, "остаток": gold,
                     "опыт стал": xp[unit_id], "потолок": int(t.unit(unit_id)["XP_NEXT"]) - 1})
    return rows


def check_training(t: sq.Tables, prof: dict, key: str, rows: list[dict] | None = None) -> None:
    """Первый шаг обязан выйти ЧАСТИЧНЫМ, последний — попасть ровно в потолок, и всё это
    внутри отпущенных ходов."""
    rows = training_schedule(t, prof) if rows is None else rows
    if not rows:
        return
    first = rows[0]
    if first["очков"] >= first["из"]:
        raise SystemExit("%s: на первом же шаге денег хватает на ВСЁ обучение (%d очков из %d) — "
                         "частичного не увидеть, казну надо уменьшить"
                         % (key, first["очков"], first["из"]))
    if not 0.2 <= first["очков"] / first["из"] <= 0.45:
        raise SystemExit("%s: первый шаг покупает %d очков из %d (%.0f %%), а нужна примерно "
                         "треть" % (key, first["очков"], first["из"],
                                    100.0 * first["очков"] / first["из"]))
    for r in rows:
        if r["день"] > prof["turns"]:
            raise SystemExit("%s: шаг «%s %s» приходится на день %d при отпущенных %d ходах"
                             % (key, r["юнит"], r["режим"], r["день"], prof["turns"]))
        if r["режим"] == "full" and r["опыт стал"] != r["потолок"]:
            raise SystemExit("%s: полное обучение %s даёт опыт %d, а потолок XP_NEXT - 1 = %d"
                             % (key, r["юнит"], r["опыт стал"], r["потолок"]))


def check_training_rates(t: sq.Tables, prof: dict, key: str) -> None:
    """В расписании обучения обязаны встретиться ДВЕ разные ставки `Gunits.TRAINING_C`.

    Прогон 10.09.2026 дал точные суммы, но учил только Squire и Archer, а у них ставка одна и
    та же (4). «Цена очка берётся из таблицы» и «цена очка всегда 4» на таких данных дают одно
    и то же число, то есть измерения между ними нет. Различает только юнит с другим тарифом в
    том же отряде — и он обязан в отряде СТОЯТЬ, иначе учить его будет некого.
    """
    plan = prof.get("train", ())
    if not plan:
        return
    rates = {u.lower(): bank_gold(str(t.unit(u)["TRAINING_C"])) for u, _ in plan}
    if len(set(rates.values())) < 2:
        raise SystemExit("%s: в расписании обучения одна ставка на всех (%s) — «ставка из "
                         "Gunits» и «всегда %d» дадут одно число и не различатся"
                         % (key, ", ".join("%s=%d" % kv for kv in rates.items()),
                            next(iter(rates.values()))))
    have = {u.lower() for p in prof["parties"] for u, _ in p["units"]}
    missing = sorted(u for u in rates if u not in have)
    if missing:
        raise SystemExit("%s: учить собираемся %s, а в отряде их нет — обучать будет некого"
                         % (key, ", ".join(missing)))


def check_sites(t: sq.Tables, prof: dict, key: str) -> None:
    """Лавка: настоящий Тренер по `Lsite`, вход достижим и круг укладывается в очки хода."""
    sites = prof.get("sites", ())
    if not sites:
        return
    kinds = {int(r["ID"]): r["TEXT"] for r in dbf_read(sq.GLOBALS / "Lsite.dbf")["rows"]}
    if kinds.get(TRAINER_LSITE) != "L_TRAINER":
        raise SystemExit("%s: Lsite id %d — не L_TRAINER, а %s: строки 3 в ванили помечены "
                         "удалёнными, и настоящий Тренер именно шестой"
                         % (key, TRAINER_LSITE, kinds.get(TRAINER_LSITE)))
    move = min(int(t.unit(p["leader"])["MOVE"]) for p in prof["parties"])
    for s in sites:
        ent = site_entrance(s["xy"])
        d = min(cheb(tuple(p["xy"]), ent) for p in prof["parties"])
        if d < 2:
            raise SystemExit("%s: отряд в %d клетке от входа %s — движок засчитывает вход по "
                             "ПУТИ, а с нулевого пути его не будет" % (key, d, ent))
        if PLAIN_COST * 2 * d > move * MOVE_RESERVE:
            raise SystemExit("%s: круг до входа %s и обратно стоит %d очков при %d у лидера — "
                             "не уложиться" % (key, ent, PLAIN_COST * 2 * d, move))


# ------------------------------------------------ M12: простор под T_CAPTURE (S6, S10)

def free_room(prof: dict, seeds: list[tuple[int, int]]) -> int:
    """Сколько связной свободной равнины примыкает к пятну `seeds`.

    Свободная — проходимая, не занятая объектом и НЕ ПРЕОБРАЗОВАННАЯ (грунт Империи `tile & 7
    == 1` уже наш, и квота на него не тратится: своя клетка пропускается бесплатно, 0x5E0764).
    """
    tiles = plain_tiles(MAP_SIZE, prof)
    blocked = {xy for xy, _ in occupied_cells(prof)} | set(c4.capital_tiles()) | set(seeds)

    def ok(c: tuple[int, int]) -> bool:
        return (c not in blocked and c in tiles and passable(tiles[c])
                and tiles[c][0] & 7 != 1)

    seen: set[tuple[int, int]] = set()
    queue: deque[tuple[int, int]] = deque()
    for x, y in list(seeds):
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                c = (x + dx, y + dy)
                if ok(c) and c not in seen:
                    seen.add(c)
                    queue.append(c)
    while queue:
        x, y = queue.popleft()
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                c = (x + dx, y + dy)
                if ok(c) and c not in seen:
                    seen.add(c)
                    queue.append(c)
    return len(seen)


def capture_room(prof: dict) -> int:
    return free_room(prof, city.village_tiles(prof["village"]["xy"]))


def check_capture(t: sq.Tables, prof: dict, key: str) -> None:
    """Город под захват: уровень, гарнизон с нулевой бронёй, простор и отрыв от столицы."""
    v = prof.get("village")
    if v is None or not v.get("fight"):
        return
    if int(v["tier"]) != CAPTURE_TIER:
        raise SystemExit("%s: город уровня %s, нужен %d — иначе прибавка к броне не PROT_3"
                         % (key, v["tier"], CAPTURE_TIER))
    if len(v["garrison"]) != 3:
        raise SystemExit("%s: в гарнизоне %d юнитов, нужно три" % (key, len(v["garrison"])))
    for u in v["garrison"]:
        if int(t.unit(u)["ARMOR"] or 0) != 0:
            raise SystemExit("%s: у гарнизонного %s (%s) броня %s, нужна 0 — иначе строка "
                             "«Защита» покажет сумму, а не прибавку города"
                             % (key, u, t.name(u), t.unit(u)["ARMOR"]))
    gap = min(cheb(a, b) for a in c4.capital_tiles()
              for b in city.village_tiles(v["xy"]))
    if gap < CAPTURE_CLEAN_GAP:
        raise SystemExit("%s: столица в %d клетках от города при нужных %d — её собственное "
                         "пятно дорастёт до города, и разовая квота T_CAPTURE потратится на "
                         "уже свои клетки (ровно это и вышло на M3: отрыв был 4)"
                         % (key, gap, CAPTURE_CLEAN_GAP))
    room, need = capture_room(prof), int(gvars()["T_CAPTURE"])
    if room < need:
        raise SystemExit("%s: вокруг города свободной связной равнины %d клеток при квоте "
                         "T_CAPTURE = %d — квоте некуда тратиться" % (key, room, need))


# ------------------------------------------------ M13: сколько бросков нужно (S14)

@functools.lru_cache(maxsize=None)
def tri_chance(c: int) -> float:
    """P(успех) при пороге `c` для броска «среднее двух d100» (0x4430ae…0x4430c1)."""
    return sum(1 for a in range(100) for b in range(100) if (a + b) // 2 <= c) / 10000.0


def _binom(n: int, p: float) -> list[float]:
    return [math.comb(n, k) * p ** k * (1.0 - p) ** (n - k) for k in range(n + 1)]


@functools.lru_cache(maxsize=None)
def rolls_needed(c1: int = 80, c2: int = 20, alpha: float = 0.05) -> int:
    """Сколько попыток НА КАЖДУЮ из двух мишеней нужно, чтобы отличить треугольный бросок от
    плоского с ошибкой не выше `alpha` в обе стороны.

    Решение принимается по отношению правдоподобий двух биномов сразу — потому и «на каждую»,
    а не «всего»: два порога дают вдвое больше различающей силы, чем один.
    """
    for n in range(5, 200):
        dt = [_binom(n, tri_chance(c)) for c in (c1, c2)]
        df = [_binom(n, (c + 1) / 100.0) for c in (c1, c2)]
        bad_t = bad_f = 0.0
        for k1 in range(n + 1):
            l1 = math.log(dt[0][k1] / df[0][k1])
            for k2 in range(n + 1):
                if l1 + math.log(dt[1][k2] / df[1][k2]) <= 0.0:
                    bad_t += dt[0][k1] * dt[1][k2]
                else:
                    bad_f += df[0][k1] * df[1][k2]
        if max(bad_t, bad_f) <= alpha:
            return n
    raise SystemExit("различающего числа попыток не нашлось до 200 — проверь пороги")


def thief_shown(t: sq.Tables, target: str, category: int = CAT_MISFIT) -> int:
    """Показанный игроку шанс: `Gaction.CHANCE − лидер.NEGOTIATE`, отсечка нулём (0x5D31A1)."""
    return max(action_chance(category) - int(t.unit(target)["NEGOTIATE"] or 0), 0)


def check_thief(t: sq.Tables, prof: dict, key: str) -> None:
    """Воров хватает на статистику, пороги те самые, а мишень для яда переживает яд."""
    if not prof.get("thief_stat"):
        return
    if action_chance(CAT_MISFIT) != action_chance(CAT_POISON_STACK):
        raise SystemExit("%s: у Counterfeit Orders и Poison Party разные CHANCE — пороги "
                         "перестали совпадать, мишени подобраны под совпадение" % key)
    thieves = [p for p in prof["parties"] if p["cat"] == CAT_THIEF]
    targets = prof["neutrals"]
    need = rolls_needed()
    have = (len(thieves) // len(targets)) * prof["turns"] * THIEF_ACTS_PER_TURN
    if have < need:
        raise SystemExit("%s: %d воров за %d ходов дают %d попыток на мишень при нужных %d — "
                         "на таком числе треугольный бросок неотличим от плоского"
                         % (key, len(thieves), prof["turns"], have, need))
    shown = sorted(thief_shown(t, n["leader"]) for n in targets)
    if shown != [20, 80]:
        raise SystemExit("%s: показанные шансы мишеней %s, а мерить надо ровно 80 %% и 20 %%"
                         % (key, shown))
    poisoned = [n for n in targets if n["poison"]]
    if not poisoned:
        raise SystemExit("%s: ни одна мишень не помечена как годная под Poison Party — "
                         "наблюдения по самому отравлению не будет" % key)
    for n in poisoned:
        row = t.unit(n["leader"])
        hp, regen = int(row["HIT_POINT"]), int(row["REGEN"] or 0)
        worst = hp * int(gvars()["POISON_S"]) // 100    # пессимистичное чтение POISON_S в %
        if hp * regen // 100 < worst:
            raise SystemExit("%s: мишень %s лечится на %d в сутки, а худшее чтение POISON_S "
                             "снимает %d — многократного отравления она не переживёт"
                             % (key, t.name(n["leader"]), hp * regen // 100, worst))


# ------------------------------------------------ M14: второй игрок и его безопасность (S15)

def check_second(t: sq.Tables, prof: dict, key: str) -> None:
    """Сосед: другая играбельная раса, мирный старт, и до нас ему дальше, чем длится прогон."""
    s = prof.get("second")
    if s is None:
        if prof.get("rods") and prof.get("noble_acts"):
            raise SystemExit("%s: второго играбельного игрока нет — на одиночной карте шкалы "
                             "отношений нет вовсе, S15 непроверяем" % key)
        return
    race = grace(s["race"])
    if not race["PLAYABLE"] or s["race"].lower() == RACE_EMPIRE:
        raise SystemExit("%s: раса соседа %s не годится (играбельность %s)"
                         % (key, s["race"], race["PLAYABLE"]))
    lords = {r["LORD_ID"].lower(): r["RACE_ID"].lower()
             for r in dbf_read(sq.GLOBALS / "Glord.dbf")["rows"]}
    if lords.get(s["lord"].lower()) != s["race"].lower():
        raise SystemExit("%s: лорд %s не из расы %s" % (key, s["lord"], s["race"]))
    war, peace = int(gvars()["D_NEUTRAL"]), int(gvars()["D_PEACE"])
    left = s["relation"] + int(gvars()["D_ROD"]) + int(gvars()["D_NOBLE"]) * prof["noble_acts"]
    if not war < s["relation"] <= peace:
        raise SystemExit("%s: стартовое отношение %d вне полосы мира (%d, %d] — сосед не обязан "
                         "сидеть смирно" % (key, s["relation"], war, peace))
    if left <= 0:
        raise SystemExit("%s: после штрафов останется %d — шкала упрётся в пол и разницы не "
                         "покажет" % (key, left))
    check_second_safety(t, prof, key, s)


def check_second_safety(t: sq.Tables, prof: dict, key: str, s: dict) -> None:
    """Худший случай: у соседа армия уже на первом ходу, и он идёт к нам самым быстрым лидером.

    Даже тогда ему нужно больше ходов, чем длится прогон. Второй слой — казна соседа: пустая,
    а самый дешёвый его БОЕВОЙ лидер стоит `ENROLL_C`, то есть раньше чем через
    `ENROLL_C / INCOME` дней он вообще никого не наймёт.
    """
    rows = [r for r in dbf_read(sq.GLOBALS / "Gunits.dbf")["rows"]
            if r["RACE_ID"].lower() == s["race"].lower() and int(r["LEADER_CAT"] or 0) > 0]
    if not rows:
        raise SystemExit("%s: у расы %s нет лидеров — модель угрозы не построить" % (key, s["race"]))
    reach = max(int(r["MOVE"]) for r in rows) // PLAIN_COST      # клеток за один его ход
    theirs = capital_tiles_at(s["capital"])
    ours = [(xy, what) for xy, what in occupied_cells(prof) if what == "отряд игрока"]
    ours += [(xy, "наша столица") for xy in c4.capital_tiles()]
    for xy, what in ours:
        gap = min(cheb(xy, b) for b in theirs)
        if gap <= reach * prof["turns"]:
            raise SystemExit("%s: %s в %s стоит в %d клетках от чужой столицы, а сосед проходит "
                             "%d клеток за ход и прогон длится %d хода — успеет прийти"
                             % (key, what, xy, gap, reach, prof["turns"]))
    if bank_gold(s["bank"]) != 0:
        raise SystemExit("%s: казна соседа %s не пуста — он наймёт войско раньше срока"
                         % (key, s["bank"]))


def print_second_wave(t: sq.Tables, prof: dict) -> None:
    """Числа, которые прогон обязан увидеть: расписание обучения, простор, попытки, шкала."""
    for s in prof.get("sites", ()):
        print("   лавка %s: %s, вход %s" % (tuple(s["xy"]), s["title"], site_entrance(s["xy"])))
    for r in training_schedule(t, prof):
        print("   день %d: %-12s %-7s %3d очков из %3d за %4d золота, остаток %4d, опыт %3d "
              "из потолка %3d" % (r["день"], t.name(r["юнит"]), r["режим"], r["очков"], r["из"],
                                  r["списано"], r["остаток"], r["опыт стал"], r["потолок"]))
    v = prof.get("village")
    if v and v.get("fight"):
        gap = min(cheb(a, b) for a in c4.capital_tiles() for b in city.village_tiles(v["xy"]))
        print("   город %s уровня %d, гарнизон %d, до столицы %d клеток, свободной равнины "
              "вокруг %d при квоте T_CAPTURE %s"
              % (tuple(v["xy"]), int(v["tier"]), len(v["garrison"]), gap, capture_room(prof),
                 gvars()["T_CAPTURE"]))
    if prof.get("thief_stat"):
        thieves = [p for p in prof["parties"] if p["cat"] == CAT_THIEF]
        need = rolls_needed()
        print("   воров %d, ходов %d -> %d попыток на мишень при нужных %d"
              % (len(thieves), prof["turns"],
                 (len(thieves) // len(prof["neutrals"])) * prof["turns"] * THIEF_ACTS_PER_TURN,
                 need))
        for n in prof["neutrals"]:
            print("      мишень %-16s NEGOTIATE %2s -> показанный шанс %d %%, "
                  "P(треугольный) %.3f против плоского %.3f, яд %s"
                  % (t.name(n["leader"]), t.unit(n["leader"])["NEGOTIATE"],
                     thief_shown(t, n["leader"]), tri_chance(thief_shown(t, n["leader"])),
                     (thief_shown(t, n["leader"]) + 1) / 100.0,
                     "разрешён" if n["poison"] else "запрещён"))
    s = prof.get("second")
    if s:
        print("   сосед %s (%s), столица %s, казна %s, отношение %d -> после жезла %d -> "
              "после %d действий вора %d"
              % (s["name"], s["race"], tuple(s["capital"]), s["bank"], s["relation"],
                 s["relation"] + int(gvars()["D_ROD"]),
                 prof["noble_acts"],
                 s["relation"] + int(gvars()["D_ROD"])
                 + int(gvars()["D_NOBLE"]) * prof["noble_acts"]))


def wall_ring(xy: tuple[int, int], span: int = 4) -> list[tuple[int, int]]:
    """Кольцо гор в `span` клетках вокруг пятна города — подсадная теснота для `--selftest`."""
    tiles = city.village_tiles(xy)
    x0, y0 = min(x for x, _ in tiles), min(y for _, y in tiles)
    x1, y1 = max(x for x, _ in tiles), max(y for _, y in tiles)
    return [(x, y) for x in range(x0 - span, x1 + span + 1)
            for y in range(y0 - span, y1 + span + 1)
            if min(max(abs(x - a), abs(y - b)) for a, b in tiles) == span]
