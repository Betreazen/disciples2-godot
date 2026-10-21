#!/usr/bin/env python3
r"""Install / restore / inspect the probe build of mss32.dll in the game directory.

Three commands:

  python install.py [--game PATH] [--autobattle]   # install probe build (default)
  python install.py --restore [--game PATH]        # put the tulset 0.14.1 mss32.dll back
  python install.py --status [--game PATH]         # report what is installed, change nothing

Install backs up the currently-installed mss32.dll to "mss32.dll.v0141" the FIRST time it
runs (an existing backup is never overwritten -- it is the known-good tulset build), then
copies the freshly built probe DLL (tools/d2probe/mss32/Release/mss32.dll) over mss32.dll and
creates the "probe/" directory the DLL uses as its "am I in probe mode" switch. --autobattle
additionally drops the empty "probe/autobattle" marker file (see README.md, autobattle section);
WITHOUT --autobattle install removes that marker, so the flag always describes what is actually
installed, even over a previous install that had it.

Before making that first backup, install checks the current mss32.dll against the known
size+sha256 of tulset 0.14.1. This catches the case where someone deleted "mss32.dll.v0141"
while the probe build was already installed: without the check, a second "install" would
back up the probe DLL under the 0.14.1 name and the real original would be gone. A mismatch
refuses with an explanation; --force-backup overrides it for someone who is sure the current
mss32.dll really is 0.14.1 (a different build, say).

Restore copies "mss32.dll.v0141" back over "mss32.dll" and removes the "probe/autobattle"
marker; it does not touch "probe/" itself or any journal already written there -- those are
data, not install state.

Both install and restore refuse to run while the game process is alive: overwriting a DLL a
running exe has mapped does not take effect until restart and can leave the directory in a
mixed state. --status is read-only and does not check this.

--vanilla-globals (install only) additionally swaps game_dir/Globals for game-pristine/Globals
(renamed aside as "Globals.modded" first): a clean probe build cannot read modded Globals
tables and exits at the main menu. The swap happens only after the probe DLL and the game
directory have both been verified, so a failed install does not leave the game with vanilla
tables under the old DLL. --restore reverses it whenever "Globals.modded" is present,
independently of whether mss32.dll.v0141 exists.

--coverage (install only, schema 7.3) writes "probe/coverage.txt": the reliable function starts of
coverage/functions.tsv (built by tools/probe_coverage.py make-list) plus the exe size they belong to.
The DLL then records which functions of Discipl2.exe ran (one-shot breakpoints, probecov.cpp) into
"<journal>.cov.txt". Like --autobattle, an install WITHOUT --coverage removes the list, and --restore
removes it too.
"""
import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
MODDING_ROOT = REPO_ROOT.parent / "disciples-modding"
GAME_PRISTINE = Path(os.environ.get("D2_GAME_DIR", str(MODDING_ROOT / "game-pristine")))
DEFAULT_GAME_DIR = GAME_PRISTINE.parent / "game-dev"
PROBE_DLL = Path(__file__).resolve().parent / "mss32" / "Release" / "mss32.dll"

DLL_NAME = "mss32.dll"
BACKUP_NAME = "mss32.dll.v0141"
AUTOBATTLE_MARKER = "autobattle"
COVERAGE_LIST = "coverage.txt"
FUNCTIONS_TSV = Path(__file__).resolve().parent / "coverage" / "functions.tsv"

# --vanilla-globals: a clean (non-toolset) engine build cannot read the modded Globals/
# tables left in game-dev (Gattacks/Gunits/Gmodif/GUmodif.dbf/LModifS etc. -- see mod repo)
# and exits at the main menu. Swapping in game-pristine/Globals lets the probe build boot.
GLOBALS_DIR_NAME = "Globals"
GLOBALS_BACKUP_NAME = "Globals.modded"

# Known-good tulset 0.14.1 mss32.dll, measured once via --status on the real game-dev
# (see task-4-report.md). Backing up without checking this would let a second install, run
# after someone deletes the backup, "back up" the probe DLL under the 0.14.1 name and lose
# the real original for good.
TOOLSET_0141_SIZE = 3_119_616
TOOLSET_0141_SHA256 = "236fd899168cdeedee451504f8860da93d5869dc9936d7cbe8e5448478cb5dd6"


# What goes into mss32.dll: a commit touching these makes every older build stale. 19.10.2026 an install from
# the main checkout put a build of 30.09 over probe 7.5 (its Release/ had never been rebuilt after the merge).
BUILD_INPUTS = ("tools/d2probe/mss32", "tools/d2probe/patches")
# build.ps1 bakes `git rev-parse --short HEAD` (+dirty) into the DLL as a plain C string (probe.cpp, PROBE_GIT_HASH).
REVISION_RE = re.compile(rb"\x00([0-9a-f]{7,40})(\+dirty)?\x00")


def git(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(["git", "-C", str(REPO_ROOT), *args], capture_output=True, text=True)


def last_source_commit() -> str:
    res = git("log", "-1", "--format=%H", "--", *BUILD_INPUTS)
    if res.returncode != 0 or not res.stdout.strip():
        print(f"git log over {BUILD_INPUTS} failed: {res.stderr.strip()}", file=sys.stderr)
        sys.exit(1)
    return res.stdout.strip()


def build_revisions(dll: Path) -> list[tuple[str, bool]]:
    """Commits of this repo named by hex strings in the DLL (the build's revision; library hex tables don't resolve)."""
    found = []
    for m in REVISION_RE.finditer(dll.read_bytes()):
        rev = m.group(1).decode()
        res = git("rev-parse", "--verify", "--quiet", rev + "^{commit}")
        if res.returncode == 0:
            found.append((res.stdout.strip(), m.group(2) is not None))
    return found


def stale_build_reason(dll: Path) -> str | None:
    """None if the build contains the newest commit of the probe sources, otherwise why it must not be installed."""
    src = last_source_commit()
    revisions = build_revisions(dll)
    if not revisions:
        return (f"{dll} has no git revision of this repo -- built without tools/d2probe/build.ps1 "
                "or from another clone; rebuild: pwsh -File tools/d2probe/build.ps1")
    for rev, dirty in revisions:
        if git("merge-base", "--is-ancestor", src, rev).returncode == 0:
            if dirty:
                print(f"  note: build {rev[:8]}+dirty -- from uncommitted probe sources", file=sys.stderr)
            return None
    built = ", ".join(rev[:8] for rev, _ in revisions)
    return (f"{dll} is stale: built from {built}, but the probe sources changed in {src[:8]} "
            f"({git('log', '-1', '--format=%cs %s', src).stdout.strip()}); "
            "rebuild: pwsh -File tools/d2probe/build.ps1 (or --allow-stale to install it anyway)")


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def describe(path: Path) -> str:
    if not path.exists():
        return f"{path.name}: missing"
    return f"{path.name}: {path.stat().st_size} bytes, sha256={sha256_file(path)}"


def game_is_running() -> bool:
    res = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq Discipl2.exe", "/FO", "CSV", "/NH"],
        capture_output=True, text=True,
    )
    if res.returncode != 0:
        # Не знаем, запущена ли игра, -- молча считать "нет" нельзя: подмена DLL под живой игрой.
        print(f"tasklist failed (exit {res.returncode}): {res.stderr.strip()} -- "
              "can't tell whether Discipl2.exe is running", file=sys.stderr)
        sys.exit(1)
    return any(line.startswith('"') for line in res.stdout.splitlines())


def require_game_not_running() -> None:
    if game_is_running():
        print("Discipl2.exe is running -- close the game before install/restore.",
              file=sys.stderr)
        sys.exit(1)


def globals_paths(game_dir: Path) -> tuple[Path, Path, Path]:
    return (
        game_dir / GLOBALS_DIR_NAME,
        game_dir / GLOBALS_BACKUP_NAME,
        GAME_PRISTINE / GLOBALS_DIR_NAME,
    )


def cmd_vanilla_globals(game_dir: Path) -> None:
    globals_dir, backup, pristine = globals_paths(game_dir)
    if not pristine.is_dir():
        print(f"{pristine} not found -- can't get vanilla Globals", file=sys.stderr)
        sys.exit(1)

    if backup.exists():
        dev_gunits = globals_dir / "Gunits.dbf"
        pristine_gunits = pristine / "Gunits.dbf"
        if not dev_gunits.exists() or not pristine_gunits.exists():
            print("Gunits.dbf missing in game or pristine Globals -- can't verify the swap",
                  file=sys.stderr)
            sys.exit(1)
        if sha256_file(dev_gunits) != sha256_file(pristine_gunits):
            print(f"{backup} is present but {dev_gunits} doesn't match the pristine copy -- "
                  "the swap looks incomplete or wrong", file=sys.stderr)
            sys.exit(1)
        n = sum(1 for _ in globals_dir.iterdir())
        print(f"Globals.modded already present -- vanilla Globals already active "
              f"({n} files, Gunits.dbf verified byte-for-byte against pristine)")
        return

    if not globals_dir.is_dir():
        print(f"{globals_dir} not found", file=sys.stderr)
        sys.exit(1)
    globals_dir.rename(backup)
    shutil.copytree(pristine, globals_dir)
    n = sum(1 for _ in globals_dir.iterdir())
    print(f"Globals replaced with vanilla: {n} files copied from {pristine}")
    if not (globals_dir / "GUmodif.dbf").exists():
        print("  GUmodif.dbf absent in vanilla Globals -- this is normal (toolset-only table)")


def restore_globals(game_dir: Path) -> None:
    globals_dir, backup, _ = globals_paths(game_dir)
    if not backup.exists():
        return
    if globals_dir.exists():
        shutil.rmtree(globals_dir)
    backup.rename(globals_dir)
    print("  Globals: vanilla substitute removed, Globals.modded restored")


def write_coverage_list(probe_dir: Path) -> int:
    """probe/coverage.txt from functions.tsv: the exe size header and every reliable start."""
    lines = []
    count = 0
    for line in FUNCTIONS_TSV.read_text(encoding="utf-8").splitlines():
        if line.startswith("# exe_size "):
            lines.append(line)
        elif line and not line.startswith("#"):
            parts = line.split("\t")
            if len(parts) >= 4 and parts[3] == "1":
                lines.append(parts[0])
                count += 1
    (probe_dir / COVERAGE_LIST).write_text("\n".join(lines) + "\n", encoding="ascii")
    return count


def cmd_install(game_dir: Path, autobattle: bool, force_backup: bool,
                 vanilla_globals: bool, coverage: bool = False, allow_stale: bool = False) -> None:
    require_game_not_running()
    if not PROBE_DLL.exists():
        print(f"probe build not found: {PROBE_DLL}", file=sys.stderr)
        sys.exit(1)
    stale = stale_build_reason(PROBE_DLL)
    if stale and not allow_stale:
        print(f"refusing to install: {stale}", file=sys.stderr)
        sys.exit(1)
    if stale:
        print(f"  --allow-stale: {stale}", file=sys.stderr)
    if coverage and not FUNCTIONS_TSV.exists():
        print(f"coverage list not found: {FUNCTIONS_TSV} (tools/probe_coverage.py make-list)", file=sys.stderr)
        sys.exit(1)
    dll = game_dir / DLL_NAME
    backup = game_dir / BACKUP_NAME
    if not dll.exists():
        print(f"{dll} not found -- doesn't look like a game directory", file=sys.stderr)
        sys.exit(1)

    # After both existence checks, not before: swapping Globals and only then bailing out on a
    # missing probe DLL would leave the game half-converted, with vanilla tables and the old
    # mss32.dll, and the operator none the wiser.
    # The backup decision belongs here too: refusing after the Globals swap would leave the same
    # half-converted state.
    is_0141 = (not backup.exists() and dll.stat().st_size == TOOLSET_0141_SIZE
               and sha256_file(dll) == TOOLSET_0141_SHA256)
    if not backup.exists() and not is_0141 and not force_backup:
        print(f"  refusing to back up {dll} as tulset 0.14.1: it doesn't match "
              f"(expected {TOOLSET_0141_SIZE} bytes, sha256={TOOLSET_0141_SHA256}). "
              "This is probably already the probe build with mss32.dll.v0141 deleted -- "
              "backing it up now would lose the real 0.14.1 original for good. "
              "If you're sure the current mss32.dll IS 0.14.1 (e.g. a different build), "
              "rerun with --force-backup.", file=sys.stderr)
        sys.exit(1)

    if vanilla_globals:
        cmd_vanilla_globals(game_dir)

    print("Before install:")
    print(f"  {describe(dll)}")

    if backup.exists():
        print(f"  backup already present: {describe(backup)} -- leaving it alone")
    else:
        shutil.copy2(dll, backup)
        print(f"  backup created: {describe(backup)}")

    shutil.copy2(PROBE_DLL, dll)
    probe_dir = game_dir / "probe"
    probe_dir.mkdir(exist_ok=True)
    marker = probe_dir / AUTOBATTLE_MARKER
    if autobattle:
        marker.touch()
    else:
        # An install without --autobattle must mean no autobattle: a marker left over from a
        # previous install would silently keep forcing it, and the journal would disagree with
        # the command that produced it.
        marker.unlink(missing_ok=True)
    coverage_list = probe_dir / COVERAGE_LIST
    if coverage:
        print(f"  probe/{COVERAGE_LIST}: {write_coverage_list(probe_dir)} function starts")
    else:
        coverage_list.unlink(missing_ok=True)

    print("After install:")
    print(f"  {describe(dll)}")
    print(f"  probe/: {'ok' if probe_dir.is_dir() else 'FAILED'}")
    print(f"  probe/{AUTOBATTLE_MARKER}: {'present' if marker.exists() else 'absent'}")
    print(f"  probe/{COVERAGE_LIST}: {'present' if coverage_list.exists() else 'absent'}")


def cmd_restore(game_dir: Path) -> None:
    require_game_not_running()
    dll = game_dir / DLL_NAME
    backup = game_dir / BACKUP_NAME
    _, globals_backup, _ = globals_paths(game_dir)
    if not backup.exists() and not globals_backup.exists():
        print(f"{backup} not found -- nothing to restore (install never ran?)",
              file=sys.stderr)
        sys.exit(1)

    if backup.exists():
        print("Before restore:")
        print(f"  {describe(dll)}")

        shutil.copy2(backup, dll)
        marker = game_dir / "probe" / AUTOBATTLE_MARKER
        if marker.exists():
            marker.unlink()
        (game_dir / "probe" / COVERAGE_LIST).unlink(missing_ok=True)

        print("After restore:")
        print(f"  {describe(dll)}")
        print(f"  probe/{AUTOBATTLE_MARKER}: {'still present?!' if marker.exists() else 'removed'}")
        print("  probe/*.jsonl left untouched.")
    else:
        print(f"  {BACKUP_NAME} not found -- skipping mss32.dll restore")

    restore_globals(game_dir)


def cmd_status(game_dir: Path) -> None:
    dll = game_dir / DLL_NAME
    backup = game_dir / BACKUP_NAME
    probe_dir = game_dir / "probe"
    marker = probe_dir / AUTOBATTLE_MARKER

    _, globals_backup, _ = globals_paths(game_dir)

    print(f"game: {game_dir}")
    print(f"  {describe(dll)}")
    if dll.exists():
        revs = build_revisions(dll)
        print(f"  {DLL_NAME} revision: {', '.join(r[:8] + ('+dirty' if d else '') for r, d in revs) or 'none'}"
              f" ({'stale' if stale_build_reason(dll) else 'up to date'} against the probe sources)")
    print(f"  {describe(backup)}")
    print(f"  probe/: {'present' if probe_dir.is_dir() else 'absent'}")
    print(f"  probe/{AUTOBATTLE_MARKER}: {'present' if marker.exists() else 'absent'}")
    print(f"  probe/{COVERAGE_LIST}: {'present' if (probe_dir / COVERAGE_LIST).exists() else 'absent'}")
    print(f"  Globals: {'vanilla (Globals.modded present)' if globals_backup.exists() else 'as-is'}")
    if probe_dir.is_dir():
        journals = sorted(probe_dir.glob("*.jsonl"))
        print(f"  probe/*.jsonl: {len(journals)}")
        for j in journals:
            print(f"    {j.name}: {j.stat().st_size} bytes")
    print(f"  running: {'yes' if game_is_running() else 'no'}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                      formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--game", type=Path, default=DEFAULT_GAME_DIR,
                         help=f"game directory (default: {DEFAULT_GAME_DIR})")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--restore", action="store_true", help="put mss32.dll.v0141 back")
    mode.add_argument("--status", action="store_true", help="report current state, change nothing")
    parser.add_argument("--autobattle", action="store_true",
                         help="on install, also create the probe/autobattle marker")
    parser.add_argument("--force-backup", action="store_true",
                         help="back up the current mss32.dll as 0.14.1 even if it doesn't "
                              "match the known 0.14.1 size/sha256")
    parser.add_argument("--vanilla-globals", action="store_true",
                         help="on install, swap game_dir/Globals for game-pristine/Globals "
                              "(a clean probe build can't read modded Globals tables and "
                              "exits at the main menu); --restore undoes this too")
    parser.add_argument("--coverage", action="store_true",
                         help="on install, also write probe/coverage.txt (schema 7.3: which "
                              "functions of Discipl2.exe ran, <journal>.cov.txt)")
    parser.add_argument("--allow-stale", action="store_true",
                         help="install the probe build even if it is older than the newest commit "
                              "of its sources (or carries no revision)")
    args = parser.parse_args()

    game_dir = args.game.resolve()
    if args.status:
        cmd_status(game_dir)
    elif args.restore:
        cmd_restore(game_dir)
    else:
        cmd_install(game_dir, args.autobattle, args.force_backup, args.vanilla_globals,
                    args.coverage, args.allow_stale)
    return 0


if __name__ == "__main__":
    sys.exit(main())
