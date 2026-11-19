"""Synthetic file-tree tests; never invokes Chromium or a compiler."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location('input_snapshot', Path(__file__).with_name('input_snapshot.py'))
snapshot = importlib.util.module_from_spec(spec)
spec.loader.exec_module(snapshot)


class InputSnapshotTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='aegis-snapshot-test-')
        self.root = Path(self.temp.name)
        (self.root / 'out').mkdir()
        (self.root / 'out' / 'args.gn').write_text('target_cpu="x64"\n', encoding='utf-8')

    def tearDown(self):
        self.temp.cleanup()

    def test_stable_snapshot_is_deterministic_and_inconclusive(self):
        first = snapshot.capture(self.root)
        self.assertEqual(first, snapshot.capture(self.root))
        self.assertEqual(first['status'], 'INCONCLUSIVE')
        self.assertEqual(first['compiler_input_provenance'], 'NOT_ESTABLISHED')

    def test_changed_generated_file_changes_digest(self):
        before = snapshot.capture(self.root)['content_sha256']
        (self.root / 'out' / 'args.gn').write_text('target_cpu="arm64"\n', encoding='utf-8')
        self.assertNotEqual(before, snapshot.capture(self.root)['content_sha256'])

    def test_added_and_removed_files_change_digest(self):
        before = snapshot.capture(self.root)['content_sha256']
        (self.root / 'out' / 'extra').write_bytes(b'new')
        self.assertNotEqual(before, snapshot.capture(self.root)['content_sha256'])
        (self.root / 'out' / 'args.gn').unlink()
        self.assertNotEqual(before, snapshot.capture(self.root)['content_sha256'])

    def test_empty_directory_is_not_ignored(self):
        before = snapshot.capture(self.root)['content_sha256']
        (self.root / 'newdir').mkdir()
        self.assertNotEqual(before, snapshot.capture(self.root)['content_sha256'])

    def test_entry_limit_fails_closed(self):
        with self.assertRaisesRegex(snapshot.SnapshotError, 'ENTRY_LIMIT_EXCEEDED'):
            snapshot.capture(self.root, limit=1)

    def test_directory_race_fails_closed(self):
        real_listdir = snapshot.os.listdir
        calls = 0
        def changed(path):
            nonlocal calls
            calls += 1
            data = real_listdir(path)
            return data if calls < 3 else data + ['late-addition']
        with mock.patch.object(snapshot.os, 'listdir', side_effect=changed):
            with self.assertRaises(snapshot.SnapshotError):
                snapshot.capture(self.root)

    def test_link_or_junction_is_rejected(self):
        fake = mock.Mock(st_file_attributes=0x400, st_mode=0)
        self.assertTrue(snapshot._unsafe(fake))

    def test_invalid_compare_digest_fails(self):
        self.assertEqual(snapshot.main(['--root', str(self.root), '--compare-sha256', 'bad']), 1)

    def test_mismatched_compare_digest_fails(self):
        self.assertEqual(snapshot.main(['--root', str(self.root), '--compare-sha256', '0' * 64]), 1)


if __name__ == '__main__':
    unittest.main()
