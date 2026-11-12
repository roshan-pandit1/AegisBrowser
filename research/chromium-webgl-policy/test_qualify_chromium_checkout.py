#!/usr/bin/env python3
"""Exercise checkout prerequisite rejection using disposable, owned Git repos."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("chromium_qualification", HERE / "qualify_chromium_checkout.py")
qualification = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(qualification)


class CheckoutQualificationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="aegis-checkout-qualification-")
        self.parent = Path(self.temp.name).resolve()
        self.root = self.parent / "src"
        self.root.mkdir()
        self.git("init", "-q")
        for marker in qualification.MARKERS:
            path = self.root / marker
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("synthetic only\n", encoding="utf-8")
        self.git("add", ".")
        self.git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                 "-c", "commit.gpgsign=false", "commit", "-qm", "fixture")
        self.revision = self.git("rev-parse", "HEAD")
        for marker in (".gclient", ".gclient_entries"):
            (self.parent / marker).write_text("synthetic only\n", encoding="utf-8")

    def tearDown(self):
        self.temp.cleanup()

    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.root), *args], check=True,
                              capture_output=True, text=True, timeout=30).stdout.strip()

    def test_clean_synthetic_root_passes_prerequisites_only(self):
        self.assertEqual(qualification.inspect(self.root, self.revision), [])

    def test_wrong_revision_rejected(self):
        self.assertIn("source HEAD does not match the pinned Chromium revision",
                      qualification.inspect(self.root, "0" * 40))

    def test_dirty_and_untracked_rejected(self):
        (self.root / "DEPS").write_text("changed\n", encoding="utf-8")
        self.assertIn("source repository has tracked or untracked changes",
                      qualification.inspect(self.root, self.revision))
        self.git("checkout", "--", "DEPS")
        (self.root / "unexpected.txt").write_text("new\n", encoding="utf-8")
        self.assertIn("source repository has tracked or untracked changes",
                      qualification.inspect(self.root, self.revision))

    def test_nested_root_rejected(self):
        self.assertIn("source root is not the Git repository top level",
                      qualification.inspect(self.root / "build", self.revision))

    def test_missing_markers_and_gclient_metadata_rejected(self):
        (self.root / "DEPS").unlink()
        (self.parent / ".gclient_entries").unlink()
        errors = qualification.inspect(self.root, self.revision)
        self.assertIn("required checkout marker missing: DEPS", errors)
        self.assertIn("gclient metadata missing: .gclient_entries", errors)

    def test_shallow_clone_rejected(self):
        clone = self.parent / "shallow"
        subprocess.run(["git", "clone", "--depth=1", "-q", self.root.as_uri(), str(clone)],
                       check=True, capture_output=True, text=True, timeout=30)
        self.assertIn("source repository is shallow", qualification.inspect(clone, self.revision))

    def test_unborn_repository_rejected(self):
        unborn = self.parent / "unborn"
        unborn.mkdir()
        subprocess.run(["git", "-C", str(unborn), "init", "-q"],
                       check=True, capture_output=True, text=True, timeout=30)
        self.assertIn("source repository has no resolvable HEAD",
                      qualification.inspect(unborn, self.revision))


if __name__ == "__main__":
    unittest.main()
