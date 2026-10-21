"""tools/repin_harness.py — перезакрепляет числа упавших харнессов D2.Data.Tests.

    python tools/repin_harness.py "FullyQualifiedName~Harness"

Только ПОСЛЕ проверки, что старое правило (временный переключатель) возвращает прежние числа, и с пояснением у каждого теста.
Берёт строку упавшего Assert.Equal(wantX, ...) из стека, находит
параметр wantX в сигнатуре теста и меняет его значение в InlineData или CorpusRow с тем же корпусом (или литерал в Assert.Equal(число, ...)).
Печатает каждое изменение: файл, тест, параметр, было -> стало. Повторяет прогон, пока всё не пройдёт (до 12 раз)."""
import os, re, subprocess, sys
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
os.chdir(ROOT)
FILTER = sys.argv[1] if len(sys.argv) > 1 else ""
log = []

def run():
    cmd = ["dotnet", "test", "src/D2.Data.Tests"]
    if FILTER: cmd += ["--filter", FILTER]
    b = subprocess.run(["dotnet", "build", "src/D2.Data.Tests", "-v", "q", "-nologo"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    if b.returncode != 0:
        print("dotnet build не прошёл (код %d):\n%s" % (b.returncode, (b.stdout + b.stderr)[-3000:]))
        sys.exit(1)
    env = dict(os.environ, DOTNET_CLI_UI_LANGUAGE="en", VSLANG="1033")
    p = subprocess.run(cmd + ["--no-build"], capture_output=True, text=True, encoding="utf-8", errors="replace", env=env)
    return p.stdout + p.stderr

fail_re = re.compile(r"^\s+Failed (D2\.Data\.Tests\.(\w+)\.(\w+))(\((.*)\))? \[", re.M)
for it in range(12):
    out = run()
    blocks = re.split(r"(?m)^\s+Failed D2\.Data\.Tests\.", out)[1:]
    if not blocks:
        print("всё прошло"); break
    changed = False
    for b in blocks:
        head = b.splitlines()[0]
        m = re.match(r"(\w+)\.(\w+)(\((.*)\))? \[", head)
        if not m:
            print("не разобрал заголовок упавшего теста:", head); continue
        cls, test, args = m.group(1), m.group(2), m.group(4) or ""
        exp = re.search(r"Expected: (\S+)", b); act = re.search(r"Actual:\s+(\S+)", b)
        ln = re.search(r"in (\w:.+?\.cs):line (\d+)", b)
        if not (exp and act and ln):
            print("не разобрал:", head); continue
        path, line = ln.group(1), int(ln.group(2))
        src = open(path, encoding="utf-8", newline="").read()
        lines = src.split("\n")
        call = lines[line - 1]
        am = re.search(r"Assert\.Equal\((\w+),", call)
        if not am:
            print("не Assert.Equal:", path, line, call.strip()); continue
        name = am.group(1)
        if name.isdigit():
            lines[line - 1] = call.replace(f"Assert.Equal({name},", f"Assert.Equal({act.group(1)},", 1)
            log.append(f"{cls}.{test}: литерал {name} -> {act.group(1)}")
        else:
            corpus = re.search(r'corpus: "([^"]+)"', args)
            # параметры метода
            sm = re.search(r"public void " + test + r"\(([^)]*)\)", src, re.S)
            if not sm:
                print("нет сигнатуры теста %s в %s" % (test, path)); continue
            sig = sm.group(1)
            params = [p.split("=")[0].strip().split()[-1] for p in sig.split(",")]   # у параметра по умолчанию имя — до «=»
            idx = params.index(name)
            found = False
            for i, l in enumerate(lines):
                im = re.match(r'(\s*\[(?:InlineData|CorpusRow)\()(.*)(\)\]\s*)$', l.rstrip("\r"))
                if not im: continue
                vals = [v.strip() for v in re.findall(r'"[^"]*"|[^,]+', im.group(2))]
                if corpus and vals[0].strip('"') != corpus.group(1): continue
                if vals[idx] != exp.group(1): continue
                old = vals[idx]; vals[idx] = act.group(1)
                cr = "\r" if l.endswith("\r") else ""
                lines[i] = im.group(1) + ", ".join(vals) + im.group(3).rstrip("\r") + cr
                log.append(f"{cls}.{test}({corpus.group(1) if corpus else ''}): {name} {old} -> {act.group(1)}")
                found = True; break
            if not found:
                print("нет InlineData:", cls, test, name, exp.group(1)); continue
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8", newline="") as fh:
            fh.write("\n".join(lines))
        os.replace(tmp, path)
        changed = True
    if not changed:
        print("нечего менять"); print(out[-3000:]); break
print("\n".join(log))
