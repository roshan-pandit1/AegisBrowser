#!/usr/bin/env python3
"""Check pinned source inputs and patch applicability; never apply the patch."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import subprocess
import sys


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_root", type=Path, help="local Chromium checkout or verified source subset")
    args = parser.parse_args()
    research = Path(__file__).resolve().parent
    manifest = json.loads((research / "source-manifest.json").read_text(encoding="utf-8"))
    source_root = args.source_root.resolve(strict=True)
    if not source_root.is_dir():
        raise ValueError("source root must be a directory")
    patch_name = PurePosixPath(manifest["patch"])
    if len(patch_name.parts) != 1 or patch_name.is_absolute():
        raise ValueError("invalid patch filename")
    patch = (research / patch_name.name).resolve(strict=True)
    if patch.parent != research or digest(patch) != manifest["patch_sha256"]:
        raise ValueError("patch path or SHA-256 mismatch")
    for entry in manifest["sources"]:
        rel = PurePosixPath(entry["path"])
        if rel.is_absolute() or any(part in (".", "..") for part in rel.parts):
            raise ValueError("invalid source path")
        source = source_root.joinpath(*rel.parts).resolve(strict=True)
        if not source.is_relative_to(source_root) or not source.is_file():
            raise ValueError("source path escaped the selected root")
        if digest(source) != entry["sha256"]:
            raise ValueError("source SHA-256 mismatch: " + rel.as_posix())
    top = subprocess.run(["git", "rev-parse", "--show-toplevel"], cwd=source_root,
                         check=True, capture_output=True, text=True, timeout=30)
    if Path(top.stdout.strip()).resolve() != source_root:
        raise ValueError("source root must be the Git repository top level")
    print("SOURCE_SUBSET_HASHES: MATCH", flush=True)
    subprocess.run(["git", "apply", "--check", "--whitespace=error-all", str(patch)],
                   cwd=source_root, check=True, timeout=30)
    print("PATCH_APPLICATION_CHECK: PASS")
    print("ENGINE_BUILD_AND_ENFORCEMENT: NOT_VERIFIED")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, json.JSONDecodeError, subprocess.SubprocessError) as exc:
        print("SOURCE_PATCH_CHECK: FAILED: " + str(exc), file=sys.stderr)
        sys.exit(1)
