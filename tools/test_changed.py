#!/usr/bin/env python3
"""Run only the tests that the current changes can affect.

Run from anywhere inside the repository:

    python tools/test_changed.py                 # tests affected by uncommitted changes
    python tools/test_changed.py --fast          # also skip [Category=Slow] tests
    python tools/test_changed.py --depth 1       # quicker: follow only one level of uses
    python tools/test_changed.py --dry-run       # print the selection, run nothing
    python tools/test_changed.py --base master   # everything on this branch since it left master
    python tools/test_changed.py -- --logger "console;verbosity=detailed"   # extra dotnet test args

How it decides:
- Each changed .cs file under src/ or tests/ is a seed.
- Any file that uses a type declared in a seed is pulled in, then any file that uses a type declared in one of
  those, and so on (all the way, unless --depth). The check is by name: it over-selects a little (a shared word can
  pull in a file) but does not miss a direct or chained use.
- Every test class in a pulled-in test file runs. A test class's own names do not pull in other files.
- Guard tests and the determinism, replay and snapshot checks run with any code change: they read the whole
  repository or the whole simulation.
- A change to a project file, props, solution, shader, or tool, or a deleted .cs file, runs the full suite.
  Docs, CI and editor files under .github, .cursor and .claude are ignored.

This is for iterating. Run the full suite (`dotnet test Emberward.slnx`) before a commit.
"""

import argparse
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CODE_DIRS = ("src", "tests")
TYPE_DECL = re.compile(r"\b(?:class|struct|record|interface|enum)\s+([A-Za-z_]\w*)")
WORD = re.compile(r"[A-Za-z_]\w*")
TEST_MARK = re.compile(r"\[(?:Fact|Theory)\]")
ALWAYS_RUN = {
    "DeterminismTests", "ReplayTests", "SimulationFingerprintTests", "SnapshotRoundTripTests",
    "SnapshotCoverageTests", "MachineBehaviorTableTests", "MachinePresentationTests",
}
# A filter longer than this risks the Windows command-line limit; the full suite is then just as quick to start.
MAX_FILTER_CHARS = 20000


def git(*args: str) -> list[str]:
    result = subprocess.run(["git", *args], cwd=ROOT, check=True, capture_output=True, text=True)
    return [line for line in result.stdout.splitlines() if line]


def changed_files(base: str) -> list[str]:
    """Files that differ from the merge base with `base` (committed or not), plus untracked files."""
    try:
        start = git("merge-base", "HEAD", base)[0]
    except subprocess.CalledProcessError:
        start = "HEAD"
    return sorted(set(git("diff", "--name-only", start)) | set(git("ls-files", "--others", "--exclude-standard")))


BUILD_NAMES = {"Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.config",
               "global.json", ".editorconfig", "Emberward.slnx"}
BUILD_SUFFIXES = (".csproj", ".props", ".targets", ".slnx")


def is_build_input(path: str) -> bool:
    """True for a change that can alter what gets built or tested, so the selection cannot be trusted.

    Docs, CI and editor config under .github, .cursor and .claude do not count, nor does a .csx script at the root.
    """
    if path.endswith(".md"):
        return False
    if path.split("/")[0] in ("src", "tests") and not path.endswith(".py"):
        return True  # a non-.cs file under the code, or a .cs file that was deleted or moved out of it
    if path.startswith("tools/Emberward.ShaderCompiler/"):
        return True  # a build step that the desktop build runs
    return Path(path).name in BUILD_NAMES or path.endswith(BUILD_SUFFIXES)


def load_code() -> dict[str, tuple[set[str], set[str], bool]]:
    """Per .cs file under src/ and tests/: (words it uses, types it declares, has a test method)."""
    info = {}
    for folder in CODE_DIRS:
        for path in (ROOT / folder).rglob("*.cs"):
            if {"obj", "bin"} & set(path.parts):
                continue
            text = path.read_text(encoding="utf-8", errors="replace")
            rel = path.relative_to(ROOT).as_posix()
            info[rel] = (set(WORD.findall(text)), set(TYPE_DECL.findall(text)), bool(TEST_MARK.search(text)))
    return info


def pulled_in(seeds: set[str], info: dict, depth: int | None) -> set[str]:
    """The seeds plus every file that uses a type they declare, and so on, `depth` times (None: until nothing new)."""
    hit = set(seeds)
    names: set[str] = set()
    for f in hit:
        names |= provided_names(f, info)
    level = 0
    while depth is None or level < depth:
        more = {f for f in info if f not in hit and info[f][0] & names}
        if not more:
            break
        hit |= more
        for f in more:
            names |= provided_names(f, info)
        level += 1
    return hit


def provided_names(f: str, info: dict) -> set[str]:
    """The types a file's uses can lead to: what it declares, unless it is a test class (nothing uses those)."""
    if f.startswith("tests/") and info[f][2]:
        return set()
    return info[f][1]


def test_class_names(files, info) -> set[str]:
    names: set[str] = set()
    for f in files:
        if f.startswith("tests/") and info[f][2]:
            names |= info[f][1]
    return names


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base", default="HEAD",
                        help="compare with this ref: HEAD = uncommitted changes (default); master = everything "
                             "on this branch since it left master")
    parser.add_argument("--fast", action="store_true", help="also skip tests in Category=Slow")
    parser.add_argument("--dry-run", action="store_true", help="print the selection and exit")
    parser.add_argument("--all", action="store_true", help="run the full suite")
    parser.add_argument("--depth", type=int, default=None,
                        help="follow name uses this many levels out from the changes (default: all the way; "
                             "fewer levels run less but can miss a failure that travels through a chain of uses)")
    parser.add_argument("--files", nargs="*", help="use these changed paths instead of git (for testing)")
    parser.add_argument("extra", nargs="*", help="passed on to dotnet test (put them after --)")
    args = parser.parse_args()

    changed = args.files if args.files is not None else changed_files(args.base)
    info = load_code()

    seeds, full_reasons = set(), []
    for f in changed:
        if f in info:
            seeds.add(f)
        elif is_build_input(f):
            full_reasons.append(f)

    if args.all:
        full_reasons.append("--all")
    if not changed:
        print("No changes since the base: nothing to run.")
        return 0

    filter_expr = None
    if not full_reasons:
        hit = pulled_in(seeds, info, args.depth) if seeds else set()
        classes = test_class_names(hit, info)
        if seeds:
            # Guard and whole-simulation checks run with any code change.
            for f, (_, _, has_tests) in info.items():
                stem = Path(f).stem
                if f.startswith("tests/") and has_tests and (stem in ALWAYS_RUN or "Guard" in stem):
                    classes |= info[f][1]
        classes = sorted(classes)
        if not classes:
            print("Changes touch no test-linked code: nothing to run.")
            print(f"Changed .cs files: {len(seeds)}")
            return 0
        parts = [f"FullyQualifiedName~Emberward.Tests.{c}" for c in classes]
        filter_expr = "|".join(parts)
        if args.fast:
            filter_expr = f"({filter_expr})&Category!=Slow"
        if len(filter_expr) > MAX_FILTER_CHARS:
            full_reasons.append("the selection is too large for one filter")
            filter_expr = None

    print(f"Base: {args.base}. Changed files: {len(changed)} ({len(seeds)} .cs under src/ and tests/).")
    if full_reasons:
        print("Running the full suite because of: " + ", ".join(sorted(full_reasons)[:5]))
    else:
        print(f"Selected {len(classes)} test classes" + (" (Slow skipped)" if args.fast else "") + ":")
        for c in classes:
            print(f"  {c}")

    command = ["dotnet", "test", "Emberward.slnx"]
    if filter_expr:
        command += ["--filter", filter_expr]
    command += args.extra
    print("Command: " + " ".join(f'"{a}"' if "|" in a or "&" in a or " " in a else a for a in command))
    if args.dry_run:
        return 0
    return subprocess.run(command, cwd=ROOT).returncode


if __name__ == "__main__":
    sys.exit(main())
