#!/usr/bin/env python3
"""Acquire a disposable, version-matched CEF/Chromium checkout.

This tool does not compile CEF. A failed checkout is never deleted or retried
in place: use a new empty download root after reviewing the failed attempt.
"""
from __future__ import annotations

import argparse
import ast
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time

CEF_REVISION = "14c5a089e8452874cb1a556dc3fde26ed2d87723"
CHROMIUM_REVISION = "b859317bf11f6be47f9b7799ec690a0a42a1fb33"
CHROMIUM_REF = "refs/tags/154.0.8037.98"
CEF_BRANCH = "8037"
CEF_URL = "https://github.com/chromiumembedded/cef.git"
CHROMIUM_URL = "https://chromium.googlesource.com/chromium/src.git"
DEPOT_URL = "https://chromium.googlesource.com/chromium/tools/depot_tools.git"
SHA_RE = re.compile(r"^[0-9a-f]{40}$")
SAFE_PATH_RE = re.compile(r"^[A-Za-z]:\\[A-Za-z0-9_.\\-]+$")


class AcquisitionError(Exception):
    pass


def validate_sha(value: str) -> str:
    if not SHA_RE.fullmatch(value):
        raise AcquisitionError("EXPECTED_FULL_LOWERCASE_COMMIT_SHA")
    return value


def validate_windows_path(value: str, maximum: int = 120) -> str:
    # automate-git constructs commands internally. Restrict paths so that a
    # caller-supplied directory cannot introduce shell metacharacters.
    if not SAFE_PATH_RE.fullmatch(value) or len(value) > maximum:
        raise AcquisitionError("PATH_MUST_BE_SHORT_ASCII_ABSOLUTE_AND_SHELL_SAFE")
    if any(part in (".", "..", "") for part in value[3:].split("\\")):
        raise AcquisitionError("PATH_COMPONENT_INVALID")
    return value


def validate_compatibility(text: str) -> None:
    try:
        value = ast.literal_eval(text)
    except (SyntaxError, ValueError) as exc:
        raise AcquisitionError("CEF_COMPAT_METADATA_INVALID") from exc
    if not isinstance(value, dict) or value.get("chromium_checkout") != CHROMIUM_REF:
        raise AcquisitionError("CEF_CHROMIUM_COMPATIBILITY_MISMATCH")


def redact_dependency_json(value):
    """Reject credential-bearing URLs before a dependency snapshot is saved."""
    if isinstance(value, dict):
        return {str(k): redact_dependency_json(v) for k, v in value.items()}
    if isinstance(value, list):
        return [redact_dependency_json(v) for v in value]
    if isinstance(value, str):
        # No userinfo in any URL, including nested snapshot values.
        if re.search(r"[A-Za-z][A-Za-z0-9+.-]*://[^/\s]*@", value):
            raise AcquisitionError("DEPENDENCY_SNAPSHOT_CONTAINS_URL_USERINFO")
        return value
    return value


def checked_path(path: Path) -> Path:
    value = validate_windows_path(str(path))
    # Reject an existing symlink/junction anywhere along the path. This does
    # not defend against an administrator changing the filesystem concurrently.
    current = Path(value)
    while True:
        if current.exists() or current.is_symlink():
            stat = current.lstat()
            if current.is_symlink() or (getattr(stat, "st_file_attributes", 0) & 0x400):
                raise AcquisitionError("REPARSE_POINT_NOT_ALLOWED")
        if current.parent == current:
            break
        current = current.parent
    return Path(value)


def run(args: list[str], *, cwd: Path | None = None, env=None, timeout=120, capture=True):
    try:
        result = subprocess.run(args, cwd=str(cwd) if cwd else None, env=env,
                                timeout=timeout, check=False,
                                stdout=subprocess.PIPE if capture else None,
                                stderr=subprocess.PIPE if capture else None)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise AcquisitionError("COMMAND_START_OR_TIMEOUT_FAILED: " + Path(args[0]).name) from exc
    if result.returncode:
        # Do not echo remote URLs, credential helpers or raw environment output.
        raise AcquisitionError("COMMAND_FAILED: " + Path(args[0]).name + " exit=" + str(result.returncode))
    return result.stdout if capture else b""


def git(*args: str, cwd: Path | None = None, env=None, timeout=120) -> bytes:
    return run(["git.exe", *args], cwd=cwd, env=env, timeout=timeout)


def text(data: bytes) -> str:
    return data.decode("utf-8", errors="strict").strip()


def git_state(path: Path, remote: str, expected: str, *, require_clean: bool) -> dict:
    if not path.is_dir():
        raise AcquisitionError("GIT_CHECKOUT_MISSING: " + path.name)
    top = Path(text(git("-C", str(path), "rev-parse", "--show-toplevel"))).resolve()
    if top != path.resolve():
        raise AcquisitionError("GIT_TOPLEVEL_MISMATCH: " + path.name)
    actual_remote = text(git("-C", str(path), "remote", "get-url", "origin"))
    if actual_remote != remote:
        raise AcquisitionError("GIT_ORIGIN_MISMATCH: " + path.name)
    head = text(git("-C", str(path), "rev-parse", "HEAD"))
    if head != expected:
        raise AcquisitionError("GIT_HEAD_MISMATCH: " + path.name)
    status = git("-C", str(path), "status", "--porcelain=v1", "--untracked-files=all")
    tracked = git("-C", str(path), "status", "--porcelain=v1", "--untracked-files=no")
    difference = git("-C", str(path), "diff", "--binary", "HEAD", "--")
    if require_clean and status:
        raise AcquisitionError("GIT_WORKTREE_NOT_CLEAN: " + path.name)
    return {"commit": head, "origin": remote, "clean": not bool(status),
            "tracked_changes": bool(tracked), "status_sha256": hashlib.sha256(status).hexdigest(),
            "tracked_diff_sha256": hashlib.sha256(difference).hexdigest()}


def require_host_preflight(workspace: Path) -> dict:
    script = Path(__file__).resolve().with_name("preflight.ps1")
    if not script.is_file():
        raise AcquisitionError("PREFLIGHT_SCRIPT_MISSING")
    data = run(["powershell.exe", "-NoProfile", "-NonInteractive", "-File", str(script),
                "-BuildRoot", str(workspace), "-Stage", "Host"], timeout=120)
    try:
        report = json.loads(data.decode("utf-8-sig"))
    except (UnicodeError, ValueError) as exc:
        raise AcquisitionError("PREFLIGHT_OUTPUT_INVALID") from exc
    if report.get("qualified") is not True or report.get("stage") != "Host":
        raise AcquisitionError("HOST_PREFLIGHT_NOT_QUALIFIED")
    return report


def validate_layout(workspace: Path, download: Path) -> None:
    workspace, download = checked_path(workspace), checked_path(download)
    if not workspace.is_dir():
        raise AcquisitionError("WORKSPACE_MUST_EXIST")
    # The official tool deletes/reverts some paths while syncing. Never point
    # it at an existing checkout or outside the operator-selected workspace.
    if download.parent != workspace or download.name not in ("baseline", "instrumented"):
        raise AcquisitionError("DOWNLOAD_ROOT_MUST_BE_BASELINE_OR_INSTRUMENTED_CHILD")


def clone_pinned(destination: Path, remote: str, revision: str) -> None:
    if destination.exists():
        raise AcquisitionError("DESTINATION_ALREADY_EXISTS: " + destination.name)
    # Cloning is not retried in place; an interrupted clone is preserved.
    git("clone", "--no-checkout", "--", remote, str(destination), timeout=1800)
    git("-C", str(destination), "checkout", "--detach", revision)
    git_state(destination, remote, revision, require_clean=True)


def preflight_remote(remote: str) -> None:
    # Read-only probes only. Bounded retry never retries a mutation or hides a
    # partially completed clone/sync.
    for attempt in range(3):
        try:
            git("ls-remote", "--exit-code", remote, "HEAD")
            return
        except AcquisitionError:
            if attempt == 2:
                raise AcquisitionError("OFFICIAL_REMOTE_UNAVAILABLE")
            time.sleep(attempt + 1)


def prepare_tools(workspace: Path, depot_revision: str) -> None:
    require_host_preflight(workspace)
    depot, bootstrap = workspace / "depot_tools", workspace / "bootstrap_cef"
    if depot.exists() or bootstrap.exists():
        # Idempotence is verification only; never update an existing checkout.
        git_state(depot, DEPOT_URL, depot_revision, require_clean=True)
        git_state(bootstrap, CEF_URL, CEF_REVISION, require_clean=True)
        validate_compatibility((bootstrap / "CHROMIUM_BUILD_COMPATIBILITY.txt").read_text(encoding="utf-8"))
        print("TOOLS_ALREADY_VERIFIED_NO_CHANGES")
        return
    preflight_remote(DEPOT_URL)
    preflight_remote(CEF_URL)
    clone_pinned(depot, DEPOT_URL, depot_revision)
    clone_pinned(bootstrap, CEF_URL, CEF_REVISION)
    validate_compatibility((bootstrap / "CHROMIUM_BUILD_COMPATIBILITY.txt").read_text(encoding="utf-8"))
    print("TOOLS_VERIFIED; DEPOT_TOOL_BINARY_BOOTSTRAP=NOT_VERIFIED")


def acquire(workspace: Path, download: Path, depot_revision: str, timeout_minutes: int) -> None:
    require_host_preflight(workspace)
    if download.exists():
        raise AcquisitionError("DOWNLOAD_ROOT_ALREADY_EXISTS_USE_VERIFY_OR_NEW_ROOT")
    depot, bootstrap = workspace / "depot_tools", workspace / "bootstrap_cef"
    git_state(depot, DEPOT_URL, depot_revision, require_clean=True)
    git_state(bootstrap, CEF_URL, CEF_REVISION, require_clean=True)
    validate_compatibility((bootstrap / "CHROMIUM_BUILD_COMPATIBILITY.txt").read_text(encoding="utf-8"))
    preflight_remote(CHROMIUM_URL)
    # Workspace is explicit and path-checked; exclusively create the output.
    # No automatic retries because the upstream script may modify its tree.
    download.mkdir(exist_ok=False)
    # The upstream automator can print build-related environment variables.
    # Supply only the values needed here, rather than disclosing inherited
    # settings or accidentally changing the requested source configuration.
    filtered = ("CEF_", "GCLIENT_", "GN_", "GYP_", "DEPOT_TOOLS_")
    environment = {key: value for key, value in os.environ.items()
                   if not key.upper().startswith(filtered)}
    environment["PATH"] = str(depot) + os.pathsep + environment.get("PATH", "")
    environment["DEPOT_TOOLS_UPDATE"] = "0"
    environment["DEPOT_TOOLS_WIN_TOOLCHAIN"] = "0"
    script = bootstrap / "tools" / "automate" / "automate-git.py"
    args = [sys.executable, str(script), "--download-dir=" + str(download),
            "--depot-tools-dir=" + str(depot), "--branch=" + CEF_BRANCH,
            "--checkout=" + CEF_REVISION, "--chromium-checkout=" + CHROMIUM_REF,
            "--no-depot-tools-update", "--no-build", "--no-distrib"]
    print("STARTING_PINNED_UPSTREAM_SOURCE_SYNC; SOURCE_BUILD=NOT_RUN", flush=True)
    run(args, cwd=workspace, env=environment, timeout=timeout_minutes * 60, capture=False)
    print("SYNC_FINISHED; RUN VERIFY BEFORE BUILD", flush=True)


def verify(workspace: Path, download: Path, depot_revision: str) -> dict:
    depot, bootstrap = workspace / "depot_tools", workspace / "bootstrap_cef"
    versions = {
        "depot_tools": git_state(depot, DEPOT_URL, depot_revision, require_clean=False),
        "bootstrap_cef": git_state(bootstrap, CEF_URL, CEF_REVISION, require_clean=False),
        "cef": git_state(download / "cef", CEF_URL, CEF_REVISION, require_clean=False),
        "chromium": git_state(download / "chromium" / "src", CHROMIUM_URL, CHROMIUM_REVISION, require_clean=False),
    }
    for label in ("depot_tools", "bootstrap_cef", "cef"):
        if versions[label]["tracked_changes"]:
            raise AcquisitionError("TRACKED_TOOL_OR_CEF_CHANGES_REQUIRE_REVIEW: " + label)
    validate_compatibility((download / "cef" / "CHROMIUM_BUILD_COMPATIBILITY.txt").read_text(encoding="utf-8"))
    source_cef = download / "chromium" / "src" / "cef"
    if not source_cef.is_dir():
        raise AcquisitionError("CEF_SOURCE_COPY_MISSING")
    versions["cef_copy"] = git_state(source_cef, CEF_URL, CEF_REVISION, require_clean=False)
    if versions["cef_copy"]["tracked_changes"]:
        raise AcquisitionError("TRACKED_CEF_COPY_CHANGES_REQUIRE_REVIEW")
    # --actual reads checked-out dependencies. Fail closed if the selected
    # depot_tools version does not support structured revinfo.
    env = os.environ.copy()
    env["DEPOT_TOOLS_UPDATE"] = "0"
    env["PATH"] = str(depot) + os.pathsep + env.get("PATH", "")
    # gclient.bat uses cmd; every path component was previously constrained.
    output = download / "dependency-revisions.json"
    if output.exists():
        raise AcquisitionError("DEPENDENCY_SNAPSHOT_ALREADY_EXISTS")
    run(["cmd.exe", "/d", "/c", str(depot / "gclient.bat"), "revinfo", "--actual",
         "--output-json", str(output)], cwd=download / "chromium", env=env, timeout=180)
    try:
        parsed = redact_dependency_json(json.loads(output.read_text(encoding="utf-8-sig")))
        if not isinstance(parsed, (dict, list)) or not parsed:
            raise AcquisitionError("DEPENDENCY_SNAPSHOT_EMPTY")
    except (OSError, ValueError) as exc:
        raise AcquisitionError("DEPENDENCY_SNAPSHOT_INVALID") from exc
    snapshot_hash = hashlib.sha256(output.read_bytes()).hexdigest()
    return {"schema": "aegis-cef-source-acquisition-v1", "generated_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "versions": versions, "chromium_ref": CHROMIUM_REF,
            "dependency_revisions": {"file": output.name, "sha256": snapshot_hash,
                                     "review_before_publication": True},
            # Upstream CEF may intentionally patch Chromium. Capture the
            # actual diff; operator must compare with pinned CEF patch set.
            "chromium_patch_review": "REQUIRED" if versions["chromium"]["tracked_changes"] else "NO_TRACKED_CHANGES_OBSERVED",
            "baseline_compilation": "NOT_RUN", "instrumented_compilation": "NOT_RUN",
            "sandbox_enforcement": "NOT_RUN"}


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("tools", "checkout", "verify"))
    parser.add_argument("--workspace", required=True, help="Existing short NTFS builder root; no spaces.")
    parser.add_argument("--depot-tools-commit", required=True, help="Explicit reviewed depot_tools full SHA; CEF metadata does not pin it.")
    parser.add_argument("--download-root", help="Direct baseline or instrumented child of workspace.")
    parser.add_argument("--timeout-minutes", type=int, default=360, help="Source sync process deadline (1..720).")
    parser.add_argument("--execute", action="store_true", help="Explicitly authorize tools or checkout mutations.")
    args = parser.parse_args(argv)
    try:
        if os.name != "nt":
            raise AcquisitionError("WINDOWS_X64_REQUIRED")
        revision = validate_sha(args.depot_tools_commit)
        workspace = checked_path(Path(args.workspace))
        if not workspace.is_dir():
            raise AcquisitionError("WORKSPACE_MUST_EXIST")
        if args.action in ("tools", "checkout") and not args.execute:
            raise AcquisitionError("MUTATION_REQUIRES_EXECUTE")
        if not 1 <= args.timeout_minutes <= 720:
            raise AcquisitionError("TIMEOUT_OUT_OF_RANGE")
        if args.action == "tools":
            prepare_tools(workspace, revision)
        else:
            if not args.download_root:
                raise AcquisitionError("DOWNLOAD_ROOT_REQUIRED")
            download = checked_path(Path(args.download_root))
            validate_layout(workspace, download)
            if args.action == "checkout":
                acquire(workspace, download, revision, args.timeout_minutes)
            else:
                # verify creates only an explicitly named new dependency snapshot;
                # it never updates/reverts any source checkout.
                print(json.dumps(verify(workspace, download, revision), indent=2))
        return 0
    except (AcquisitionError, OSError, UnicodeError) as exc:
        print("SOURCE_ACQUISITION_FAILED: " + str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
