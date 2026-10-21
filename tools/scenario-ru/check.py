"""Проверка перевода сценариев: валидность и полнота языкового слоя.

  py tools/scenario-ru/check.py                     все файлы data/scenarios/lang/ru
  py tools/scenario-ru/check.py "'Greenskins'" ...  только эти сценарии
  py tools/scenario-ru/check.py --complete ...      ещё и ошибка на каждый текст без перевода

Ошибки (код возврата 1): не тот Format/Id/Language, ключ, которого нет в сценарии, пустой текст, потерянная или лишняя
подстановка %ИМЯ%. Предупреждения (не роняют): нет кириллицы (не переведено?), английский текст начинается строкой
говорящего («Имя \\n"…"»), а перевод — нет, пропало число из оригинала (50%, 3 дня), различие числа абзацев \\n\\n,
текст длиннее самого длинного текста Руссобит-М того же поля (не влезет в окно там, где влезает оригинал), буква «ё» и
длинное тире «—» (в строках игры их нет).
"""
import argparse
import re
import sys

import scentexts as st

NUMBER = re.compile(r"\d+")
SPEAKER = re.compile(r"^[^\n\"]{1,40} ?\n")
_limits = None


def kind(key):
    """Вид поля для предела длины: name, event, site:name, stack…"""
    parts = key.split(":")
    return "site:" + parts[2] if parts[0] == "site" else parts[0]


def limits():
    """Самый длинный текст Руссобит-М каждого вида поля — он влезает в окно оригинала."""
    global _limits
    if _limits is None:
        _limits = {}
        for sid in st.scenario_ids():
            f = st.read_lang(sid, "ru")
            if f and f.get("Source") == "Руссобит-М":
                for k, v in f["Texts"].items():
                    _limits[kind(k)] = max(_limits.get(kind(k), 0), len(v))
    return _limits


def check(sid, lang, complete):
    errors, warnings = [], []
    f = st.read_lang(sid, lang)
    en = dict(st.english(sid))
    if f is None:
        if complete and en:
            errors.append("нет файла перевода (%d текстов)" % len(en))
        return errors, warnings, 0, len(en)
    if f.get("Format") != st.FORMAT:
        errors.append("Format %r" % f.get("Format"))
    if f.get("Id") != sid:
        errors.append("Id %r, а файл — %r" % (f.get("Id"), sid))
    if f.get("Language") != lang:
        errors.append("Language %r" % f.get("Language"))
    texts = f.get("Texts")
    if not isinstance(texts, dict):
        errors.append("нет Texts")
        return errors, warnings, 0, len(en)
    for key, ru in texts.items():
        if key not in en:
            errors.append("%s: ключа нет в сценарии" % key)
            continue
        if not isinstance(ru, str) or not ru.strip():
            errors.append("%s: пустой текст" % key)
            continue
        orig = en[key]
        if sorted(st.PLACEHOLDER.findall(orig)) != sorted(st.PLACEHOLDER.findall(ru)):
            errors.append("%s: подстановки %s -> %s" % (key, st.PLACEHOLDER.findall(orig), st.PLACEHOLDER.findall(ru)))
        if st.LATIN.search(orig) and not st.CYRILLIC.search(ru):
            warnings.append("%s: нет кириллицы: %r" % (key, ru[:60]))
        if key.startswith("event:") and SPEAKER.match(orig) and not SPEAKER.match(ru):
            warnings.append("%s: в оригинале строка говорящего (%r), в переводе нет" % (key, SPEAKER.match(orig).group(0).strip()))
        lost = set(NUMBER.findall(orig)) - set(NUMBER.findall(ru))
        if lost:
            warnings.append("%s: пропали числа %s" % (key, sorted(lost)))
        if f.get("Source") != "Руссобит-М":
            limit = limits().get(kind(key))
            if limit and len(ru) > limit:
                warnings.append("%s: %d знаков — длиннее самого длинного у Руссобит-М (%d)" % (key, len(ru), limit))
            if "ё" in ru or "Ё" in ru or "—" in ru:
                warnings.append("%s: «ё» или «—» — в строках игры их нет" % key)
        if orig.count("\n\n") != ru.count("\n\n") and len(orig) > 200:
            warnings.append("%s: абзацев %d -> %d" % (key, orig.count("\n\n") + 1, ru.count("\n\n") + 1))
    missing = [k for k in en if not (texts.get(k) or "").strip()]
    if complete:
        errors.extend("%s: нет перевода" % k for k in missing)
    return errors, warnings, len(en) - len(missing), len(en)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("scenario", nargs="*")
    ap.add_argument("--lang", default="ru")
    ap.add_argument("--complete", action="store_true", help="текст без перевода — ошибка")
    ap.add_argument("--quiet", action="store_true", help="без предупреждений")
    a = ap.parse_args()
    ids = a.scenario or st.scenario_ids()
    bad = done = total = 0
    for sid in ids:
        errors, warnings, ok, n = check(sid, a.lang, a.complete)
        done += ok
        total += n
        for e in errors:
            print("ОШИБКА %s: %s" % (sid, e))
        if not a.quiet:
            for w in warnings:
                print("внимание %s: %s" % (sid, w))
        bad += bool(errors)
    print("сценариев %d, с ошибками %d; переведено текстов %d из %d" % (len(ids), bad, done, total))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
