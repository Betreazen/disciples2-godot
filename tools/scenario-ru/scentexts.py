"""Ключи и файлы языкового слоя сценариев — то же устройство, что D2.Data.Play.ScenarioTexts (C#).

Слой: data/scenarios/lang/<язык>/<сценарий>.json, плоская таблица «ключ -> текст» поверх английского файла сценария
data/scenarios/<сценарий>.json и его карточки в data/game/scenarios.json (квест) или data/game/campaigns.json (глава саги).

Ключи (порядок — как в файле сценария):
  name, description, briefing, briefingLong, winText, loseText  карточка: выбор сценария, брифинг, эпилог
  player:<id>                                                    имя лорда игрока
  site:<id>:name, site:<id>:desc                                 город, площадка (лавка, храм, башня мага…), руина
  stack:<id>                                                     имя лидера отряда на карте
  template:<id>                                                  имя лидера отряда, которого создаст событие
  event:<id>:<n>                                                 текст n-го эффекта события: окно (Popup) или новая цель
                                                                 (ChangeObjective)
Пустые и пробельные тексты в слой не входят. Ключи должны совпадать с C# (тест ScenarioTextsTests.RussianFiles_MatchTheirScenarios
проверяет файлы слоя по сценариям той же схемой).
"""
import json
import os
import re

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
SCENARIOS = os.path.join(REPO, "data", "scenarios")
GAME = os.path.join(REPO, "data", "game")
FORMAT = "d2-port-scenario-texts"
VERSION = 1

CARD = [("name", "Name"), ("description", "Description"), ("briefing", "Briefing"), ("briefingLong", "BriefingLong"),
        ("winText", "WinText"), ("loseText", "LoseText")]
TEXT_EFFECTS = ("Popup", "ChangeObjective")
PLACEHOLDER = re.compile(r"%[A-Z][A-Z_0-9]*%")
CYRILLIC = re.compile("[А-Яа-яЁё]")
LATIN = re.compile("[A-Za-z]")


def _load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


_cards = None


def cards():
    """Карточки всех сценариев по id: квесты и главы саг."""
    global _cards
    if _cards is None:
        _cards = {}
        for s in _load(os.path.join(GAME, "scenarios.json")):
            _cards[s["Id"].lower()] = s
        for c in _load(os.path.join(GAME, "campaigns.json")):
            for ch in c["Chapters"]:
                _cards.setdefault(ch["Scenario"]["Id"].lower(), ch["Scenario"])
    return _cards


def scenario_ids():
    return sorted(os.path.splitext(f)[0] for f in os.listdir(SCENARIOS) if f.endswith(".json"))


def kind_of(sid):
    """«сага» — глава кампании (S1xxSC0000), иначе «квест»."""
    return "сага" if re.fullmatch(r"S\d{3}SC\d{4}", sid) else "квест"


def english(sid):
    """Переводимые тексты сценария на языке оригинала: список (ключ, текст) в порядке файла."""
    out = []

    def add(key, text):
        if text and text.strip():
            out.append((key, text))

    card = cards().get(sid.lower())
    if card:
        for key, field in CARD:
            add(key, card.get(field, ""))
    d = _load(os.path.join(SCENARIOS, sid + ".json"))
    m = d["Looks"]["Map"]
    for p in m["Players"].values():
        add("player:" + p["Id"], p.get("Name", ""))
    for s in m.get("Sites", []):
        add("site:%s:name" % s["Id"], s.get("Name", ""))
        add("site:%s:desc" % s["Id"], s.get("Description", ""))
    for s in d["Looks"].get("Stacks", []):
        add("stack:" + s["Id"], s.get("LeaderName", ""))
    for t in d["Events"].get("Templates", {}).values():
        add("template:" + t["Id"], t.get("Name", ""))
    for e in d["Events"]["Events"]:
        for i, x in enumerate(e["Effects"]):
            if x.get("Kind") in TEXT_EFFECTS:
                add("event:%s:%d" % (e["Id"], i), x.get("Text", ""))
    return out


def lang_path(sid, lang):
    return os.path.join(SCENARIOS, "lang", lang, sid + ".json")


def read_lang(sid, lang):
    """Файл перевода как dict (Format, Version, Id, Language, Source, Texts); нет файла — None."""
    path = lang_path(sid, lang)
    if not os.path.exists(path):
        return None
    return _load(path)


def write_lang(sid, lang, source, texts):
    """Записать перевод: тексты в порядке английского файла (ключи вне сценария выбрасываются), UTF-8, \\n."""
    order = [k for k, _ in english(sid)]
    body = {k: texts[k] for k in order if k in texts and texts[k].strip()}
    doc = {"Format": FORMAT, "Version": VERSION, "Id": sid, "Language": lang, "Source": source, "Texts": body}
    path = lang_path(sid, lang)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(doc, ensure_ascii=False, indent=2) + "\n")
    return path
