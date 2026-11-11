#!/usr/bin/env python3
"""Exercise a tampered input against the read-only source-subset verifier."""
import json
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import tempfile


def main() -> int:
    if len(sys.argv) != 2:
        raise ValueError("expected a verified source-subset path")
    research = Path(__file__).resolve().parent
    manifest = json.loads((research / "source-manifest.json").read_text(encoding="utf-8"))
    source = Path(sys.argv[1]).resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix="aegis-source-negative-") as temporary:
        root = Path(temporary)
        target = root / "nested"
        target.mkdir()
        for entry in manifest["sources"]:
            relative = PurePosixPath(entry["path"])
            if relative.is_absolute() or any(part in (".", "..") for part in relative.parts):
                raise ValueError("invalid source path")
            destination = target / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source / relative, destination)
        subprocess.run(["git", "init", "-q", str(root)], check=True, timeout=30)
        nested = subprocess.run([sys.executable, str(research / "verify_source_subset.py"), str(target)],
                                text=True, capture_output=True, timeout=30)
        if nested.returncode == 0 or "Git repository top level" not in nested.stderr:
            raise RuntimeError("nested source root was not rejected as expected")
        changed = target / manifest["sources"][0]["path"]
        with changed.open("ab") as stream:
            stream.write(b"X")
        result = subprocess.run([sys.executable, str(research / "verify_source_subset.py"), str(target)],
                                text=True, capture_output=True, timeout=30)
        if result.returncode == 0 or "source SHA-256 mismatch" not in result.stderr:
            raise RuntimeError("tampered source was not rejected as expected")
    print("NESTED_SOURCE_ROOT_REJECTED: PASS")
    print("TAMPERED_SOURCE_REJECTED: PASS")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.SubprocessError) as exc:
        print("SOURCE_NEGATIVE_TEST: FAILED: " + str(exc), file=sys.stderr)
        sys.exit(1)
