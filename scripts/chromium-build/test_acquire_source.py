"""Offline guard tests only: no checkout, downloads, CEF build, or sandbox test."""
import importlib.util
from pathlib import Path
import unittest
from unittest import mock

MODULE = Path(__file__).with_name("acquire_source.py")
spec = importlib.util.spec_from_file_location("acquire_source", MODULE)
acquire = importlib.util.module_from_spec(spec)
spec.loader.exec_module(acquire)


class AcquisitionGuards(unittest.TestCase):
    def test_requires_full_lowercase_commit(self):
        self.assertEqual(acquire.validate_sha("a" * 40), "a" * 40)
        for value in ("a" * 39, "A" * 40, "z" * 40, "a" * 40 + "\n"):
            with self.subTest(value=value), self.assertRaises(acquire.AcquisitionError):
                acquire.validate_sha(value)

    def test_rejects_unsafe_paths(self):
        self.assertEqual(acquire.validate_windows_path(r"C:\build\baseline"), r"C:\build\baseline")
        for path in (r"C:\build with spaces", r"C:\build\..\other", r"\\server\share", r"C:\build&bad", "relative"):
            with self.subTest(path=path), self.assertRaises(acquire.AcquisitionError):
                acquire.validate_windows_path(path)

    def test_compatibility_metadata_must_match(self):
        acquire.validate_compatibility("{'chromium_checkout': 'refs/tags/154.0.8037.98'}")
        for value in ("{}", "{'chromium_checkout': 'refs/tags/154.0.8037.99'}", "not python"):
            with self.subTest(value=value), self.assertRaises(acquire.AcquisitionError):
                acquire.validate_compatibility(value)

    def test_credential_url_in_snapshot_rejected(self):
        with self.assertRaises(acquire.AcquisitionError):
            acquire.redact_dependency_json({"nested": ["https://user:secret@example.invalid/path"]})
        self.assertEqual(acquire.redact_dependency_json({"url": "https://example.invalid/path"}), {"url": "https://example.invalid/path"})

    def test_remote_probe_is_bounded(self):
        with mock.patch.object(acquire, "git", side_effect=acquire.AcquisitionError("failed")) as git, \
             mock.patch.object(acquire.time, "sleep") as sleep:
            with self.assertRaises(acquire.AcquisitionError):
                acquire.preflight_remote("https://example.invalid/repo")
            self.assertEqual(git.call_count, 3)
            self.assertEqual(sleep.call_count, 2)

    def test_existing_clone_is_never_mutated(self):
        destination = mock.Mock()
        destination.exists.return_value = True
        destination.name = "existing"
        with mock.patch.object(acquire, "git") as git, self.assertRaises(acquire.AcquisitionError):
            acquire.clone_pinned(destination, acquire.CEF_URL, acquire.CEF_REVISION)
        git.assert_not_called()


if __name__ == "__main__":
    unittest.main(verbosity=2)
