"""Read-only inventory consistency check; never dispatches tests or scripts.

This check is not an authorization mechanism or a build attestation. Run only
after its source, interpreter and standard-library behavior have been audited.
"""

import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys


ROOT_NAMES = (
    "tests",
    "scripts",
    "AegisBrowser.Tests",
    "AegisBrowser.BrowserTests",
    "AegisBrowser.FilteringTests",
    ".github/workflows",
)
EXCLUDED_DIRECTORIES = frozenset((
    ".git",
    "aegisbrowser/bin", "aegisbrowser/obj",
    "aegisbrowser.core/bin", "aegisbrowser.core/obj",
    "aegisbrowser.tests/bin", "aegisbrowser.tests/obj",
    "aegisbrowser.browsertests/bin", "aegisbrowser.browsertests/obj",
    "aegisbrowser.filteringtests/bin", "aegisbrowser.filteringtests/obj",
    "tests/acceptance/dependencycompat/bin", "tests/acceptance/dependencycompat/obj",
))
RESERVED_DIRECTORIES = frozenset(("bin", "obj", ".git"))
SOURCE_EXTENSIONS = frozenset((
    ".ps1", ".psm1", ".psd1", ".ps1xml", ".pssc", ".psrc",
    ".py", ".sh", ".bash", ".bat", ".cmd", ".js", ".mjs", ".cjs",
    ".ts", ".tsx", ".jsx", ".rb", ".pl", ".vbs", ".wsf",
    ".cs", ".csproj", ".cpp", ".hpp", ".h", ".c", ".fs", ".fsproj",
    ".vb", ".vbproj", ".csx", ".fsx", ".go", ".rs", ".java",
    ".props", ".targets", ".proj", ".yml", ".yaml", ".sql", ".html", ".htm",
))
CONTROL_FILENAMES = frozenset((
    "global.json", "nuget.config", "package.json", "makefile", "dockerfile",
))
INVENTORY_RELATIVE = "tests/governance/test-safety-inventory.json"
CATEGORIES = frozenset((
    "UNKNOWN",
    "SAFE_READ_ONLY",
    "SAFE_WORKSPACE",
    "ISOLATED_ENVIRONMENT_REQUIRED",
    "EXPLICIT_AUTHORIZATION_REQUIRED",
))
REQUIRED_FIELDS = (
    "profile", "required_environment", "required_authorization",
    "expected_filesystem_changes", "process_operations",
    "network_operations", "privileges", "basis", "execution_status",
)
REPARSE_ATTRIBUTE = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)


def fail(message):
    # Never echo source contents, argument values, or discovered path names.
    print("INVENTORY_CHECK=FAIL reason=" + message)
    return 1


def object_without_duplicates(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key")
        result[key] = value
    return result


def is_reparse(path):
    info = os.lstat(path)
    return stat.S_ISLNK(info.st_mode) or bool(
        getattr(info, "st_file_attributes", 0) & REPARSE_ATTRIBUTE
    )


def canonical_relative(value):
    if (not isinstance(value, str) or not value or not value.isascii()
            or "\\" in value or ":" in value):
        raise ValueError("invalid inventory path")
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in ("", ".", "..") for part in path.parts):
        raise ValueError("invalid inventory path")
    if str(path) != value:
        raise ValueError("non-canonical inventory path")
    return value


def sha256_file(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(1024 * 1024):
            digest.update(block)
    return digest.hexdigest().upper()


def scan_error(_):
    raise OSError("source discovery failed")


def operation_is_declared(arguments):
    # This fixed declaration permits only source inventory inspection.
    # It does not grant permission for Profile B, Profile C or direct scripts.
    return arguments == ["--profile=A:inspect-inventory"]


def compare_inventory(records, observed_hashes):
    """Pure comparison; records and digests are supplied in memory."""
    missing = len(set(records) - set(observed_hashes))
    new = len(set(observed_hashes) - set(records))
    unknown = 0
    changed = 0
    for key in set(records) & set(observed_hashes):
        _, item = records[key]
        if item["category"] == "UNKNOWN" or any(
            item[field].strip().upper().startswith("UNKNOWN")
            for field in REQUIRED_FIELDS
        ):
            unknown += 1
        expected = item.get("observed_sha256")
        if not isinstance(expected, str) or not re.fullmatch(r"[A-F0-9]{64}", expected):
            changed += 1
        elif observed_hashes[key] != expected:
            changed += 1
    return {"missing": missing, "new": new, "unknown": unknown, "changed": changed}


def comparison_is_clean(result):
    return all(result[name] == 0 for name in ("missing", "new", "unknown", "changed"))


def skip_generated_directory(path, workspace):
    relative = path.relative_to(workspace).as_posix().casefold()
    if path.name.casefold() in RESERVED_DIRECTORIES:
        if relative not in EXCLUDED_DIRECTORIES:
            raise ValueError("unexpected generated directory")
        return True
    return False


def main():
    if not operation_is_declared(sys.argv[1:]):
        return fail("operation_not_declared")
    script = Path(__file__).absolute()
    workspace = script.parents[2]
    try:
        for ancestor in (workspace, *workspace.parents):
            if is_reparse(ancestor):
                return fail("workspace_ancestor_is_reparse")
        inventory_path = workspace / INVENTORY_RELATIVE
        if script.relative_to(workspace).as_posix() != "tests/governance/check_inventory.py":
            return fail("checker_path_changed")
        for component in (
            workspace / "tests",
            workspace / "tests/governance",
            script,
            inventory_path,
        ):
            if is_reparse(component):
                return fail("checker_or_inventory_path_is_reparse")
        with inventory_path.open("r", encoding="utf-8-sig") as stream:
            inventory = json.load(stream, object_pairs_hook=object_without_duplicates)
        if inventory.get("schema_version") != 1:
            return fail("unsupported_schema")
        discovery = inventory.get("discovery")
        if not isinstance(discovery, dict):
            return fail("invalid_discovery")
        if set(discovery.get("roots", ())) != set(ROOT_NAMES):
            return fail("discovery_roots_changed")
        exclusions = discovery.get("excluded_directories")
        if not isinstance(exclusions, list) or {
            entry.casefold() for entry in exclusions if isinstance(entry, str)
        } != EXCLUDED_DIRECTORIES or len(exclusions) != len(EXCLUDED_DIRECTORIES):
            return fail("discovery_exclusions_changed")
        if discovery.get("include_all_files_in_roots") is not True:
            return fail("discovery_scope_changed")
        if discovery.get("excluded_control_files") != [INVENTORY_RELATIVE]:
            return fail("control_exclusion_changed")
        if set(discovery.get("extra_source_extensions", ())) != SOURCE_EXTENSIONS:
            return fail("source_scope_changed")
        if set(discovery.get("extra_control_filenames", ())) != CONTROL_FILENAMES:
            return fail("control_scope_changed")
        entries = inventory.get("entries")
        if not isinstance(entries, list):
            return fail("invalid_entries")

        records = {}
        for item in entries:
            if not isinstance(item, dict):
                return fail("invalid_entry")
            relative = canonical_relative(item.get("path"))
            key = relative.casefold()
            if key in records:
                return fail("duplicate_path")
            category = item.get("category")
            if category not in CATEGORIES:
                return fail("invalid_category")
            for field in REQUIRED_FIELDS:
                if not isinstance(item.get(field), str) or not item[field]:
                    return fail("missing_classification_field")
            evidence = item.get("evidence")
            if not isinstance(evidence, list) or not evidence or any(
                not isinstance(tag, str) or not tag for tag in evidence
            ):
                return fail("missing_evidence")
            records[key] = (relative, item)

        observed = {}
        for name in ROOT_NAMES:
            root = workspace / name
            if is_reparse(root) or not root.is_dir():
                return fail("root_missing_or_reparse")
            for parent, directories, files in os.walk(root, followlinks=False, onerror=scan_error):
                parent_path = Path(parent)
                for child in tuple(directories):
                    path = parent_path / child
                    if is_reparse(path):
                        return fail("directory_reparse")
                    if skip_generated_directory(path, workspace):
                        directories.remove(child)
                for child in files:
                    path = parent_path / child
                    if is_reparse(path):
                        return fail("file_reparse")
                    relative = path.relative_to(workspace).as_posix()
                    if relative == INVENTORY_RELATIVE:
                        continue
                    canonical_relative(relative)
                    key = relative.casefold()
                    if key in observed:
                        return fail("discovered_path_collision")
                    observed[key] = (relative, path)

        # Catch new source, scripts and build definitions outside test roots.
        # Only exact known build-output directories are excluded.
        root_keys = {name.casefold() for name in ROOT_NAMES}
        for parent, directories, files in os.walk(workspace, followlinks=False, onerror=scan_error):
            parent_path = Path(parent)
            for child in tuple(directories):
                path = parent_path / child
                if is_reparse(path):
                    return fail("directory_reparse")
                relative = path.relative_to(workspace).as_posix()
                if skip_generated_directory(path, workspace) or relative.casefold() in root_keys:
                    directories.remove(child)
            for child in files:
                path = parent_path / child
                if (path.suffix.casefold() not in SOURCE_EXTENSIONS
                        and path.name.casefold() not in CONTROL_FILENAMES):
                    continue
                if is_reparse(path):
                    return fail("file_reparse")
                relative = canonical_relative(path.relative_to(workspace).as_posix())
                key = relative.casefold()
                if key in observed:
                    return fail("discovered_path_collision")
                observed[key] = (relative, path)

        observed_hashes = {key: sha256_file(path) for key, (_, path) in observed.items()}
        result = compare_inventory(records, observed_hashes)
        print(
            "INVENTORY_COUNTS"
            f" recorded={len(records)} discovered={len(observed)}"
            f" unknown={result['unknown']} new={result['new']}"
            f" missing={result['missing']} changed={result['changed']}"
        )
        if not comparison_is_clean(result):
            return fail("unclassified_or_stale")
        print("INVENTORY_CHECK=PASS scope=recorded_paths_only authorization=NOT_ESTABLISHED")
        return 0
    except (OSError, ValueError, TypeError, KeyError, json.JSONDecodeError):
        return fail("inspection_error")


if __name__ == "__main__":
    sys.exit(main())
