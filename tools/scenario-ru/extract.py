"""Непереведённые тексты сценариев — для переводчика.

  py tools/scenario-ru/extract.py --stats                       сводка: сколько текстов и знаков, сколько переведено
  py tools/scenario-ru/extract.py "'Greenskins'" [--out todo.json] [--all] [--lang ru]
      {ключ: английский текст} для ключей без перевода (с --all — для всех); без --out — в stdout.
  py tools/scenario-ru/extract.py --merge done.json "'Greenskins'" [--source "машинный перевод"] [--lang ru]
      влить {ключ: перевод} в data/scenarios/lang/<язык>/<сценарий>.json (поверх уже лежащих текстов, порядок — как в сценарии).
  py tools/scenario-ru/extract.py --reuse "'Orc Champion'" "'Orc Champion_advanced'" [--source …]
      перенести перевод другого сценария там, где ключ и английский текст совпадают (вариант той же карты), — только непереведённое.

Ключи и формат — scentexts.py (то же, что D2.Data.Play.ScenarioTexts).
"""
import argparse
import json
import sys

import scentexts as st


def todo(sid, lang, everything):
    have = (st.read_lang(sid, lang) or {}).get("Texts", {})
    return {k: v for k, v in st.english(sid) if everything or not have.get(k)}


def stats(lang):
    rows = []
    for sid in st.scenario_ids():
        en = st.english(sid)
        f = st.read_lang(sid, lang)
        have = (f or {}).get("Texts", {})
        left = [(k, v) for k, v in en if not have.get(k)]
        rows.append((sid, st.kind_of(sid), (f or {}).get("Source", "-"), len(en), sum(len(v) for _, v in en),
                     len(left), sum(len(v) for _, v in left)))
    print("%-34s %-5s %-22s %6s %8s %6s %8s" % ("сценарий", "вид", "источник", "строк", "знаков", "осталось", "знаков"))
    for r in rows:
        print("%-34s %-5s %-22s %6d %8d %6d %8d" % r)
    print("всего: строк %d, знаков %d; осталось строк %d, знаков %d" % (
        sum(r[3] for r in rows), sum(r[4] for r in rows), sum(r[5] for r in rows), sum(r[6] for r in rows)))


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("scenario", nargs="*", help="id сценария (имя файла data/scenarios без .json)")
    ap.add_argument("--lang", default="ru")
    ap.add_argument("--all", action="store_true", help="все тексты, а не только непереведённые")
    ap.add_argument("--out", help="файл для {ключ: английский текст}")
    ap.add_argument("--stats", action="store_true")
    ap.add_argument("--merge", help="файл {ключ: перевод} — влить в слой")
    ap.add_argument("--source", default="машинный перевод", help="поле Source нового файла перевода")
    ap.add_argument("--reuse", help="сценарий, чей перевод перенести по совпадению ключа и английского текста")
    a = ap.parse_args()
    if a.stats:
        stats(a.lang)
        return 0
    if not a.scenario:
        ap.error("нужен id сценария")
    if a.reuse:
        if len(a.scenario) != 1:
            ap.error("--reuse — ровно один сценарий")
        sid = a.scenario[0]
        src_en = dict(st.english(a.reuse))
        src_ru = (st.read_lang(a.reuse, a.lang) or {}).get("Texts", {})
        old = st.read_lang(sid, a.lang) or {}
        texts = dict(old.get("Texts", {}))
        moved = {k: src_ru[k] for k, v in st.english(sid) if not texts.get(k) and src_en.get(k) == v and src_ru.get(k)}
        texts.update(moved)
        path = st.write_lang(sid, a.lang, old.get("Source") or a.source, texts)
        print("%s: перенесено %d, всего %d текстов" % (path, len(moved), len(texts)))
        return 0
    if a.merge:
        if len(a.scenario) != 1:
            ap.error("--merge — ровно один сценарий")
        sid = a.scenario[0]
        with open(a.merge, encoding="utf-8") as f:
            done = json.load(f)
        known = {k for k, _ in st.english(sid)}
        unknown = [k for k in done if k not in known]
        if unknown:
            print("ключей нет в сценарии: %s" % ", ".join(unknown[:10]), file=sys.stderr)
            return 1
        old = st.read_lang(sid, a.lang) or {}
        texts = dict(old.get("Texts", {}))
        texts.update({k: v for k, v in done.items() if isinstance(v, str) and v.strip()})
        path = st.write_lang(sid, a.lang, old.get("Source") or a.source, texts)
        print("%s: %d текстов" % (path, len(texts)))
        return 0
    result = {}
    for sid in a.scenario:
        part = todo(sid, a.lang, a.all)
        result.update(part if len(a.scenario) == 1 else {sid + "|" + k: v for k, v in part.items()})
    text = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    if a.out:
        with open(a.out, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        print("%s: %d текстов, %d знаков" % (a.out, len(result), sum(len(v) for v in result.values())))
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
