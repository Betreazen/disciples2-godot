#!/usr/bin/env python3
r"""Опись `Discipl2.exe` для отчёта о полноте порта: классы RTTI по областям порта и функции по классам.

Только читает: exe (pefile + capstone), список процедур Ghidra (выгрузка REA `list_procedures`), опись RTTI и группы аудита
`.analysis/rea/`. Ничего не пишет, кроме файла `--json`, если он задан.

    python tools/port_status_count.py --exe ../disciples-modding/game-pristine/Discipl2.exe \
        --procs <выгрузка REA list_procedures (bundle .json или строки "0xADDR имя")> \
        --rea-dir ../disciples-godot/.analysis/rea [--json out.json]

Метод (docs/port-status-20261016.md, раздел «Функции»):
1. Функции — процедуры Ghidra без внешних, переходников (thunk) и обработчиков исключений `Catch@`/`Unwind@` плюс цели vtable, которых
   Ghidra не выделила (вызываются только виртуально).
2. vtable — по Complete Object Locator RTTI (`vftable[-1]`), записи — подряд указатели в `.text` до следующего локатора.
3. Хозяин функции: (а) класс, в vtable которого она стоит (одна область — эта область; несколько — по большинству классов);
   (б) класс, адрес vtable которого функция пишет (конструктор, деструктор); (в) иначе — по вызывающим: все размеченные вызывающие
   одной области — та же область, нескольких — «общие». Повторять до неподвижной точки. Статический CRT и
   библиотеки по адресам не отделяются (обработчики исключений Ghidra лежат в конце `.text`, CRT — вперемешку с кодом игры): их
   функции попадают в «общие» (вызываются из нескольких областей) или «без хозяина».
4. Классы, перечисленные в `REMAINING` (частично / нет на 16.10), получают свои функции тем же способом, но с меткой класса.
Это оценка: невиртуальные помощники, вызываемые из нескольких областей, уходят в «общие»; ложные вызовы при линейном разборе
возможны, но цель обязана быть началом функции.
"""
from __future__ import annotations

import argparse
import bisect
import collections
import json
import re
import struct
import sys
from pathlib import Path

AREAS = {
    "battle": "Бой",
    "world_visitors": "Мир: мутации CVisitor",
    "world_effects": "Мир: эффекты CEffect",
    "world_msgs": "Мир: команды *Msg",
    "world_objects": "Мир: объекты карты, правила, перечисления",
    "events": "События, кампании",
    "magic": "Магия, предметы, вор, дипломатия",
    "ai": "ИИ на карте",
    "ui": "Интерфейс (окна, меню, задачи, энциклопедия)",
    "gfx": "Графика, анимации, ролики",
    "sound": "Звук",
    "saves": "Сохранения, потоки, файлы",
    "data": "Таблицы данных (DBF), типы из таблиц",
    "hotseat": "«Горячий стул»",
    "network": "Сеть (не переносим)",
    "core": "Ядро клиент-сервер, очередь команд, уведомления (заменено GameSession)",
    "service": "Служебное: исключения, шаблоны, CRT-классы",
}

# Частично / нет на 16.10 после веток port/logic, port/anim, port/uiaudio, fix/reveal (docs/port-status-20261016.md, раздел 5:
# было 30 классов, осталось 11). Имя — как в rtti-classes.txt.
REMAINING = {
    "CCmdChatMsg": "частично: чат игрока DLG_CHAT нет",
    "IAiReaction": "частично: отдельного механизма реакций нет",
    "CAiReactionNull": "частично", "CAiReactionOper": "частично", "CAiReactionStrat": "частично", "CAiReactionTactic": "частично",
    "CChatInterf": "нет: DLG_CHAT",
    "CScrollTextAnim": "нет (титры — по решению пользователя)",
    "CLoadingIntf": "нет: экран загрузки", "CMenuQuickLoad": "нет", "CMenuFlashWait": "нет",
}

NETWORK = re.compile(r"^(C|I)?(Net|MqNet)|DPLobby|GameSpy|Multi|Protocol|CMenuSession|CMenuLobby|CMenuQuickLobby|"
                     r"CMenus\w+Msg|CMidChatMsg|StreamNet|StreamCountNet|StreamEnvNet|^CMidClient|^CMidServer|IMidServerBuilder")
CORE = re.compile(r"^(CMidDataCache2|CMidObjectLock|CMidObjectNotify|INotify|Notify\d@|CMqUI|CMqThread|CMqFps|IMqUI|"
                  r"CMidCommandQueue|ICanIgnore@|IUpdate@CMidCommandQueue|CCommandCanIgnore|CoreCommandUpdate|IMidMsgSender|"
                  r"IMidTask|ITask$|ITaskManagerHolder|CTaskManager|CMidFreeTask|CMidNullTask|CFreeTask|CNullTask|CPhase|CUIManager)")
SOUND = re.compile(r"Audio|Sound|Music")
GFX = re.compile(r"^(C|I)?(2D|C2D|MidAnim|Anim|Bink|Image2|Img|Dib|Display(DDraw|D3D)|Iso|FlatMap|Terrain|Border|Fog|FF|RAD|IRAD|"
                 r"Renderer|DX|Texture|Face|Icon|Banner|MultiLayer|Cursor|Formatted|FormatString|Tooltip|TwoSpeed|MapClouds|Mq|"
                 r"Factory|Ground|TileVariation|Avoid|Background|Empty(Image)|Blank|Bordered|Color|City2D|CityLoaders|ImageDisplay|"
                 r"FullScreenBink|ScenEndAnim|Surface|PackerLoader|Store2Loader|Image@|IdManager@|SurfaceDecomp|LineArray|StlLineArray)|"
                 r"exceptionPng|CMenuFullScreenAnim|^ICity2DImage|^ICityLoaders|^IFlatMapImage|^IFormat|^IIconImg|^IFaceImg|^IFilter@|"
                 r"IImageLoader|IPresentation|IImpl@CColor")
DATA = re.compile(r"^(T[A-Z]|CDB|IDB|CProxyCodeBase|MqDbException|Field4|Str4|cvsgm|eem)")
SAVES = re.compile(r"Stream|LoadGame|SaveGame|CMenuLoad|StdFile|CLogFile|read_x@|write_open_x@|IStdFileImpl")
SERVICE = re.compile(r"^(os_|type_info|exception|CAutoDialogException|CBFunctorDispatch|ILogError@|ITextLoader@|CMidAutoDlg)")


def area_of(name: str, group: str) -> str:
    """Область порта по имени класса и группе аудита."""
    fixed = {"ai": "ai", "battle": "battle", "events": "events", "magic_items_noble_diplo": "magic",
             "world-visitors": "world_visitors", "world-effects": "world_effects", "world-msgs": "world_msgs"}
    if group in fixed:
        return fixed[group]
    if "Hotseat" in name:
        return "hotseat"
    if group == "templates":
        inner = name
        if re.search(r"CAi|Ai[A-Z]", inner): return "ai"
        if re.search(r"CBat|Battle", inner): return "battle"
        if re.search(r"Net|Command|Notify|Msg", inner): return "network" if "Net" in inner else "core"
        if re.search(r"Stream", inner): return "saves"
        if re.search(r"Enum", inner): return "data"
        if re.search(r"Menu|Interf|Task|Image|Display|Dlg|Widget|Button|Picture|Spin|Toggle|Steal|Enroll|PostTraining|ListBox|Edit", inner):
            return "ui"
        return "service"
    if name.startswith("CTest") or name in ("ITestCondition", "IEventEffect", "IEffectResult"):
        return "events"
    if NETWORK.search(name): return "network"
    if CORE.search(name): return "core"
    if SERVICE.search(name): return "service"
    if SAVES.search(name) and not name.startswith("CMenuLoadCampaign") and "Custom" not in name: return "saves"
    if SOUND.search(name): return "sound"
    if DATA.search(name): return "data"
    if GFX.search(name) and "Interf" not in name: return "gfx"
    if group == "world":
        if "Interf" in name or name.startswith(("LBDisplay", "LBoxImage")): return "ui"
        return "world_objects"
    if group in ("ui_anim_audio", "net_infra"):
        if re.match(r"^(CHealUnit|CReviveUnit|CDismissUnit|CEnrollCheck|CDynLevelUnit|CTrainingUnit|CUnitGenerator|IUnitGenerator|"
                    r"CUnitPosValidator|CPlayerBuildings|CPlayerKnownSpells|CRefreshInfo|CScenarioInfo|CScenarioVisitor|CCapital$|"
                    r"CFortification|CMergedCatalog|CScensCatalog|CCustomCampaignCatalog|IGameCatalog|ICatalogValidate|CIdManager|"
                    r"IPathFinder|IUnitGroup|IMapElement|Iterator@IMidgardObjectMap|ICarryOver|IResetStackExt|CGroupAreaManagement|"
                    r"CRacePlayerDesc|CRaceLeaderNames|CInventoryCounter|CLordLeaderUpg|CTurnSummary$|select|INobleActionResult|"
                    r"ISpellResult|IResultSender|CResultSender|CBranchHolder|CStreamBits|CStreamRegisterBase)", name):
            return "world_objects"
        return "ui"
    return "service"


def load_classes(rea: Path) -> dict[str, tuple[str, str]]:
    """Класс → (группа аудита, область порта)."""
    out = {}
    for f in sorted((rea / "groups").glob("*.txt")):
        g = f.stem
        for n in f.read_text(encoding="utf-8").split():
            if g == "world" and n in out:
                continue
            out[n] = (g, area_of(n, g))
    return out


def load_procs(path: Path):
    """(обычные функции, все начала процедур) из выгрузки REA."""
    regular, starts = set(), set()
    if path.suffix == ".json":
        recs = json.loads(path.read_text(encoding="utf-8"))["records"][0]["normalized_result"]
        rows = [(r["address"], r["value"], r["procedure"]) for r in recs]
    else:
        rows = [(a, n, {"external": False, "thunk": False}) for a, n in (l.split(" ", 1) for l in path.read_text().splitlines() if l)]
    for addr, name, kind in rows:
        if addr.startswith("EXTERNAL") or kind.get("external"):
            continue
        a = int(addr, 16)
        starts.add(a)
        if not kind.get("thunk") and not name.startswith(("Catch@", "Unwind@", "Catch_All@")):
            regular.add(a)
    return regular, starts


def label_functions(exe: Path, procs: Path, rea: Path) -> dict:
    """Функции exe и их хозяева (шаги 1–3 метода выше): {"funcs", "area", "class", "vtable", "ctor", "regular", "starts",
    "virtual_only", "vtables", "entry", "cls_area", "text"}. Общий разбор для `analyse` и свода покрытия зонда
    (`tools/probe_coverage.py`)."""
    import capstone
    import pefile

    pe = pefile.PE(str(exe), fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    img = pe.get_memory_mapped_image()
    secs = {s.Name.rstrip(b"\0").decode(): (base + s.VirtualAddress, base + s.VirtualAddress + s.Misc_VirtualSize) for s in pe.sections}
    text = secs[".text"]
    entry = base + pe.OPTIONAL_HEADER.AddressOfEntryPoint
    u32 = lambda va: struct.unpack_from("<I", img, va - base)[0]

    tds = {}
    for pat in (b".?AV", b".?AU"):
        i = img.find(pat)
        while i >= 0:
            end = img.find(b"\0", i)
            name = img[i + 4:end].decode("latin1")
            tds[base + i - 8] = name[:-2] if name.endswith("@@") else name
            i = img.find(pat, end)
    cols = {}
    for sec in (".rdata", ".data"):
        a, b = secs[sec]
        for va in range(a, b - 20, 4):
            sig, off, _cd, ptd, pchd = struct.unpack_from("<5I", img, va - base)
            if sig == 0 and ptd in tds and off < 0x10000 and (secs[".rdata"][0] <= pchd < secs[".data"][1]):
                cols[va] = tds[ptd]
    vtables = {}
    for sec in (".rdata", ".data"):
        a, b = secs[sec]
        for va in range(a, b - 4, 4):
            if u32(va) not in cols:
                continue
            ents, q = [], va + 4
            while text[0] <= u32(q) < text[1] and u32(q) not in cols:
                ents.append(u32(q))
                q += 4
                if u32(q) in cols:
                    break
            vtables[va + 4] = (cols[u32(va)], ents)

    regular, starts = load_procs(procs)
    virtual_only = {f for _, e in vtables.values() for f in e} - starts
    funcs = regular | virtual_only
    bounds = sorted(starts | virtual_only)
    def owner_fn(va):
        i = bisect.bisect_right(bounds, va) - 1
        return bounds[i] if i >= 0 else None

    classes = load_classes(rea)
    cls_area = {n: a for n, (_g, a) in classes.items()}
    # (а) vtable
    by_vt = collections.defaultdict(set)
    for name, ents in vtables.values():
        for f in ents:
            by_vt[f].add(name)
    # (б) запись адреса vtable (конструктор/деструктор)
    by_ctor = collections.defaultdict(set)
    blob = bytes(img[text[0] - base:text[1] - base])
    for vva, (name, _e) in vtables.items():
        key = struct.pack("<I", vva)
        i = blob.find(key)
        while i >= 0:
            f = owner_fn(text[0] + i)
            if f in funcs:
                by_ctor[f].add(name)
            i = blob.find(key, i + 1)
    # граф вызовов
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    callers = collections.defaultdict(set)
    for k, f in enumerate(bounds):
        if f not in funcs or not (text[0] <= f < text[1]):
            continue
        end = bounds[k + 1] if k + 1 < len(bounds) else text[1]
        code = img[f - base:min(end, text[1]) - base]
        for ins in md.disasm_lite(code, f):
            if ins[2] in ("call", "jmp") and ins[3].startswith("0x"):
                t = int(ins[3], 16)
                if t in funcs and t != f:
                    callers[t].add(f)

    def label(names, key):
        vals = collections.Counter(key(n) for n in names if key(n))
        if not vals:
            return None
        top = vals.most_common()
        if len(top) > 1 and top[0][1] == top[1][1]:
            return "shared"
        return top[0][0]

    def propagate(key):
        lab = {}
        for f in funcs:
            if f in by_vt:
                lab[f] = label(by_vt[f], key) or "shared"
            elif f in by_ctor:
                lab[f] = label(by_ctor[f], key) or "shared"
        changed = True
        while changed:
            changed = False
            for f in funcs:
                if f in lab:
                    continue
                cs = {lab[c] for c in callers.get(f, ()) if c in lab}
                if not cs:
                    continue
                cs.discard("shared")
                new = cs.pop() if len(cs) == 1 else "shared"
                lab[f] = new
                changed = True
        return lab

    area_lab = propagate(lambda n: cls_area.get(n, "service"))
    class_lab = propagate(lambda n: n)
    return {"funcs": funcs, "area": area_lab, "class": class_lab, "vtable": by_vt, "ctor": by_ctor, "regular": regular,
            "starts": starts, "virtual_only": virtual_only, "vtables": vtables, "entry": entry, "cls_area": cls_area,
            "text": text, "callers": callers}


def analyse(exe: Path, procs: Path, rea: Path) -> dict:
    ctx = label_functions(exe, procs, rea)
    funcs, area_lab, class_lab = ctx["funcs"], ctx["area"], ctx["class"]
    by_vt, by_ctor, cls_area = ctx["vtable"], ctx["ctor"], ctx["cls_area"]
    regular, starts, virtual_only, vtables, entry = (ctx["regular"], ctx["starts"], ctx["virtual_only"], ctx["vtables"],
                                                     ctx["entry"])
    by_area = collections.Counter(area_lab.get(f, "unowned") for f in funcs)
    seeded = collections.Counter()
    for f in funcs:
        if f in by_vt or f in by_ctor:
            seeded[area_lab.get(f)] += 1
    remaining = collections.Counter(class_lab.get(f) for f in funcs if class_lab.get(f) in REMAINING)
    cls_count = collections.Counter(a for a in cls_area.values())
    rem_area = collections.Counter()
    for f in funcs:
        c = class_lab.get(f)
        if c in REMAINING:
            rem_area[cls_area[c]] += 1
    return {
        "procedures_ghidra": {"regular": len(regular), "all_starts": len(starts)},
        "virtual_only": len(virtual_only), "functions": len(funcs), "vtables": len(vtables), "entry": hex(entry),
        "classes_by_area": dict(cls_count), "functions_by_area": dict(by_area), "seeded_by_area": dict(seeded),
        "remaining_functions": dict(remaining), "remaining_by_area": dict(rem_area),
        "remaining_classes_found": sorted(n for n in REMAINING if n in cls_area),
        "remaining_classes_missing": sorted(n for n in REMAINING if n not in cls_area),
    }


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--exe", type=Path, required=True)
    ap.add_argument("--procs", type=Path, required=True)
    ap.add_argument("--rea-dir", type=Path, required=True)
    ap.add_argument("--json", type=Path)
    a = ap.parse_args(argv)
    r = analyse(a.exe, a.procs, a.rea_dir)
    if a.json:
        a.json.write_text(json.dumps(r, ensure_ascii=False, indent=1), encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    print(f"функций {r['functions']} (Ghidra {r['procedures_ghidra']['regular']} + только через vtable {r['virtual_only']}), "
          f"vtable {r['vtables']}, entry {r['entry']}")
    print("область | классов | функций (из них по vtable/конструктору)")
    for k in list(AREAS) + ["shared", "unowned"]:
        print(f"{AREAS.get(k, k)} | {r['classes_by_area'].get(k, 0)} | {r['functions_by_area'].get(k, 0)} ({r['seeded_by_area'].get(k, 0)})")
    print("осталось (частично/нет):", sum(r["remaining_functions"].values()), "функций; по областям", r["remaining_by_area"])
    print("по классам:", r["remaining_functions"])
    if r["remaining_classes_missing"]:
        print("нет в описи RTTI:", r["remaining_classes_missing"])
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
