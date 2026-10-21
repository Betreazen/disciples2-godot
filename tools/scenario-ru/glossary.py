"""Словарь терминов для перевода сценариев -> tools/scenario-ru/glossary.ru.json.

  py tools/scenario-ru/glossary.py

Разделы (английский -> русский):
  races, lords, units, spells, items, landmarks, buildings — имена из таблиц игры (data/rules/*.json, NAME_TXT) по строкам
      интерфейса data/ui/strings.en.json / strings.ru.json (русификация Руссобит-М): так перевод совпадает с карточками и окнами;
  names — имена собственные из уже переведённых сценариев (город, лавка, руина, лидер отряда, лорд, шаблон): самый частый
      русский вариант и сколько раз он встретился (ключи слоя сведены по id, а не угаданы);
  lore — боги, народы, герои сюжета: проверенные формы из русских текстов сценариев и строк интерфейса (см. LORE ниже);
  phrases — повторяющиеся тексты (эпилог победы и поражения, реплики торговцев и магов в описаниях площадок): самый частый
      русский вариант Руссобит-М для того же английского текста.
"""
import collections
import json
import os
import sys

import scentexts as st

RULES = os.path.join(st.REPO, "data", "rules")
UI = os.path.join(st.REPO, "data", "ui")
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "glossary.ru.json")

# Сюжетные имена: формы — из строк интерфейса (приоритет) и текстов Руссобит-М (у Руссобит-М встречаются оба «Бетрезен» и
# «Бесрезен» — берётся интерфейсный «Бетрезен»).
LORE = {
    "Highfather": "Всевышний (Всевышний Отец)",
    "Bethrezen": "Бетрезен",
    "Mortis": "Мортис",
    "Gallean": "Галлеан",
    "Solonielle": "Солониэль",
    "Wotan": "Вотан",
    "Nevendaar": "Невендаар",
    "Ragnarok": "Рагнарек",
    "Empire": "Империя",
    "The Empire": "Империя",
    "Mountain Clans": "Горные Кланы (в названии расы — Кланы Гор)",
    "Clans": "Кланы",
    "Legions of the Damned": "Легионы Проклятых",
    "the Damned": "Проклятые",
    "Undead Hordes": "Орды Нежити",
    "Undead": "Нежить",
    "Elven Alliance": "Эльфийский Союз",
    "Elves": "Эльфы",
    "Dwarves": "Гномы",
    "Dwarf": "Гном",
    "Humans": "Люди",
    "Greenskins": "Зеленокожие",
    "Orcs": "Орки",
    "Neutrals": "Нейтральные",
    "Uther": "Утер",
    "Demon Uther": "Утер-демон",
    "Demosthene": "Демосфен",
    "Hubert de Lalye": "Хьюберт де Лейли",
    "Morok Cloudkeeper": "Морок Повелитель Туч",
    "Milord": "Милорд",
    "Master": "Господин (у нежити — Госсподин, с растяжкой «с»)",
    "Great Wars": "Великие войны",
    "Lord": "Повелитель / Владыка (по контексту)",
    "Capital": "Столица",
    "quest": "квест (у Руссобит-М) или задание",
}

TABLES = [("units", "Gunits"), ("spells", "Gspells"), ("items", "GItem"), ("landmarks", "GLmark"), ("buildings", "Gbuild"),
          ("lords", "Glord"), ("races", "Grace")]


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def table_names(en, ru, table):
    out = {}
    for row in load(os.path.join(RULES, table + ".json"))["rows"]:
        tid = (row.get("NAME_TXT") or "").upper()
        e, r = (en.get(tid) or "").strip(), (ru.get(tid) or "").strip()
        if e and r and e not in out:
            out[e] = r
    return dict(sorted(out.items(), key=lambda p: p[0].lower()))


def russobit_pairs():
    """(ключ, английский, русский) по всем сценариям с текстом Руссобит-М."""
    for sid in st.scenario_ids():
        f = st.read_lang(sid, "ru")
        if not f or f.get("Source") != "Руссобит-М":
            continue
        en = dict(st.english(sid))
        for key, ru in f["Texts"].items():
            if key in en:
                yield key, en[key].strip(), ru.strip()


def phrases():
    seen = collections.defaultdict(collections.Counter)
    for key, e, r in russobit_pairs():
        if key in ("winText", "loseText") or key.endswith(":desc"):
            seen[e][r] += 1
    return {e: c.most_common(1)[0][0] for e, c in sorted(seen.items(), key=lambda p: -sum(p[1].values()))
            if sum(c.values()) >= 2}


def scenario_names():
    seen = collections.defaultdict(collections.Counter)
    for sid in st.scenario_ids():
        f = st.read_lang(sid, "ru")
        if not f or f.get("Source") != "Руссобит-М":
            continue
        en = dict(st.english(sid))
        for key, ru in f["Texts"].items():
            if key.startswith(("player:", "stack:", "template:")) or key.endswith(":name"):
                e = en.get(key, "").strip()
                if e and e != ru.strip():
                    seen[e][ru.strip()] += 1
    return {e: (c.most_common(1)[0][0] if len(c) == 1 else "%s (%d из %d)" % (c.most_common(1)[0][0], c.most_common(1)[0][1], sum(c.values())))
            for e, c in sorted(seen.items(), key=lambda p: p[0].lower())}


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    en, ru = load(os.path.join(UI, "strings.en.json")), load(os.path.join(UI, "strings.ru.json"))
    glossary = {"lore": LORE}
    for name, table in TABLES:
        glossary[name] = table_names(en, ru, table)
    glossary["names"] = scenario_names()
    glossary["phrases"] = phrases()
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(glossary, ensure_ascii=False, indent=1) + "\n")
    print("%s: %s" % (OUT, ", ".join("%s %d" % (k, len(v)) for k, v in glossary.items())))
    return 0


if __name__ == "__main__":
    sys.exit(main())
