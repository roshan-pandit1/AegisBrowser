#!/usr/bin/env python3
"""Read-only qualification of a candidate Chromium source root; never build or patch."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

PINNED_CHROMIUM = "b859317bf11f6be47f9b7799ec690a0a42a1fb33"
MARKERS = ("DEPS", "build/config/BUILDCONFIG.gn", "third_party/blink/renderer/core/BUILD.gn")


def git(root: Path, *args: str) -> str:
    env = os.environ.copy()
    env["GIT_OPTIONAL_LOCKS"] = "0"
    return subprocess.run(["git", "-c", "core.fsmonitor=false", "-C", str(root), *args],
                          check=True, capture_output=True, text=True, timeout=90, env=env).stdout.strip()


def inspect(root: Path, expected: str = PINNED_CHROMIUM) -> list[str]:
    """Return prerequisite errors; success is not a dependency or toolchain audit.

    The expected argument permits synthetic repository tests; the CLI never accepts
    an alternate revision and always uses the pinned Chromium revision.
    """
    errors = []
    root = root.resolve(strict=True)
    if not root.is_dir():
        return ["source root is not a directory"]
    top = Path(git(root, "rev-parse", "--show-toplevel")).resolve(strict=True)
    if top != root:
        return ["source root is not the Git repository top level"]
    try:
        head = git(root, "rev-parse", "HEAD")
    except subprocess.CalledProcessError:
        head = None
        errors.append("source repository has no resolvable HEAD")
    if head is not None and head != expected:
        errors.append("source HEAD does not match the pinned Chromium revision")
    if git(root, "rev-parse", "--is-shallow-repository") != "false":
        errors.append("source repository is shallow")
    if git(root, "status", "--porcelain=v1", "--untracked-files=all"):
        errors.append("source repository has tracked or untracked changes")
    for marker in MARKERS:
        path = (root / marker).resolve()
        if not path.is_relative_to(root) or not path.is_file():
            errors.append("required checkout marker missing: " + marker)
    for marker in (".gclient", ".gclient_entries"):
        if not (root.parent / marker).is_file():
            errors.append("gclient metadata missing: " + marker)
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_root", type=Path)
    root = parser.parse_args().source_root
    errors = inspect(root)
    print("SOURCE_CHECK_SCOPE: main repository tracked and nonignored untracked files only")
    for error in errors:
        print("CHECKOUT_PREREQUISITE_FAILED: " + error)
    if errors:
        print("SOURCE_CHECKOUT: INCOMPLETE")
        print("DEPENDENCY_REVISIONS: NOT_VERIFIED")
        print("ENGINE_BUILD_AND_ENFORCEMENT: NOT_RUN")
        return 1
    manifest = json.loads((Path(__file__).resolve().parent / "source-manifest.json").read_text(encoding="utf-8"))
    if manifest.get("chromium_revision") != PINNED_CHROMIUM:
        raise ValueError("manifest Chromium revision does not match the pinned revision")
    subprocess.run([sys.executable, str(Path(__file__).resolve().parent / "verify_source_subset.py"), str(root)],
                   check=True, timeout=90)
    print("SOURCE_PREREQUISITES_AND_PATCH_CHECK: PASS")
    print("DEPENDENCY_REVISIONS: NOT_VERIFIED (gclient metadata presence is not dependency validation)")
    print("BUILDER_QUALIFICATION: NOT_VERIFIED")
    print("ENGINE_BUILD_AND_ENFORCEMENT: NOT_RUN")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, json.JSONDecodeError, subprocess.SubprocessError) as exc:
        print("CHECKOUT_QUALIFICATION: FAILED: " + str(exc), file=sys.stderr)
        sys.exit(1)
