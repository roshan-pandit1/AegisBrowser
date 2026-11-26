"""Read-only artifact/SBOM cross-check; not a complete CycloneDX schema validator."""

import argparse
import hashlib
import json
import re
import zipfile
from pathlib import Path


def fail(message):
    raise SystemExit("FAIL: " + message)


def digest(stream):
    value = hashlib.sha256()
    for block in iter(lambda: stream.read(1024 * 1024), b""):
        value.update(block)
    return value.hexdigest().upper()


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--sbom", required=True)
parser.add_argument("--archive", required=True)
parser.add_argument("--expected-archive-sha256", required=True)
args = parser.parse_args()

expected = args.expected_archive_sha256.upper()
if not re.fullmatch(r"[A-F0-9]{64}", expected):
    fail("expected archive digest must be SHA-256")
with Path(args.archive).open("rb") as stream:
    if digest(stream) != expected:
        fail("archive digest differs")

with Path(args.sbom).open(encoding="utf-8") as stream:
    bom = json.load(stream)
if bom.get("bomFormat") != "CycloneDX" or bom.get("specVersion") != "1.6":
    fail("unexpected CycloneDX identity")
components = bom.get("components")
dependencies = bom.get("dependencies")
if not isinstance(components, list) or not isinstance(dependencies, list):
    fail("components or dependencies are missing")
root = bom.get("metadata", {}).get("component", {}).get("bom-ref")
refs = [component.get("bom-ref") for component in components]
if not root or any(not ref for ref in refs) or len(refs) != len(set(refs)) or root in refs:
    fail("component identifiers are missing or duplicated")
valid_refs = set(refs)
valid_refs.add(root)
for dependency in dependencies:
    if dependency.get("ref") not in valid_refs or any(
        ref not in valid_refs for ref in dependency.get("dependsOn", [])
    ):
        fail("dependency references are unresolved")

files = {}
packages = 0
unmapped = 0
for component in components:
    hashes = component.get("hashes", [])
    sha = [item.get("content", "").upper() for item in hashes if item.get("alg") == "SHA-256"]
    if len(sha) != 1 or not re.fullmatch(r"[A-F0-9]{64}", sha[0]):
        fail("component SHA-256 is missing or malformed")
    if component.get("type") == "file":
        name = component.get("name", "")
        if not name.startswith("AegisBrowser/") or name in files:
            fail("artifact path is invalid or duplicated")
        properties = {item.get("name"): item.get("value") for item in component.get("properties", [])}
        if properties.get("aegis:location") != name:
            fail("file location does not match its artifact path")
        if str(properties.get("aegis:source", "")).startswith("UNVERIFIED"):
            unmapped += 1
        files[name] = sha[0]
    elif component.get("type") == "library":
        packages += 1
    else:
        fail("unexpected component type")

with zipfile.ZipFile(args.archive) as archive:
    entries = [entry for entry in archive.infolist() if not entry.is_dir()]
    names = [entry.filename for entry in entries]
    if len(names) != len(set(names)) or set(names) != set(files):
        fail("artifact and SBOM file inventories differ")
    for entry in entries:
        with archive.open(entry) as stream:
            if digest(stream) != files[entry.filename]:
                fail("artifact file hash differs from its SBOM entry")

print(f"PASS: artifact SHA-256 and {len(files)} SBOM file hashes match")
print(f"PASS: {packages} package components and dependency references are structurally present")
print(f"UNVERIFIED: {unmapped} file origins lack a byte-identical examined package member")
print("NOT_RUN: full CycloneDX JSON-schema validation and complete embedded dependency graph")
