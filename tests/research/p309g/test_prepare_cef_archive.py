"""Synthetic tests for the pinned CEF archive preparation boundary.

These tests do not authenticate or execute CEF. They build small disposable
archives and deliberately pass their own digests to the internal test API.
"""
import hashlib
import importlib.util
import io
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest import mock
import sys

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location(
    'p309h_archive', Path(__file__).with_name('prepare_cef_archive.py'))
archive_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(archive_module)


class ArchiveTests(unittest.TestCase):
    ROOT = 'test-cef'

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='p309h-archive-test-')
        self.base = Path(self.temp.name)
        self.archive = self.base / 'input.tar.bz2'
        self.dest = self.base / 'output'

    def tearDown(self):
        self.temp.cleanup()

    @staticmethod
    def sha(data):
        return hashlib.sha256(data).hexdigest()

    def build(self, entries):
        with tarfile.open(self.archive, 'w:bz2') as tar:
            for name, kind, payload in entries:
                item = tarfile.TarInfo(name)
                if kind == 'directory':
                    item.type = tarfile.DIRTYPE
                    tar.addfile(item)
                elif kind == 'symlink':
                    item.type = tarfile.SYMTYPE
                    item.linkname = payload
                    tar.addfile(item)
                elif kind == 'hardlink':
                    item.type = tarfile.LNKTYPE
                    item.linkname = payload
                    tar.addfile(item)
                else:
                    item.size = len(payload)
                    tar.addfile(item, io.BytesIO(payload))
        return archive_module.digest(self.archive)

    def prepare(self, entries, expected=None, archive_hash=None, **limits):
        actual_hash = self.build(entries)
        return archive_module.prepare(
            self.archive, self.dest, archive_hash or actual_hash, self.ROOT,
            expected if expected is not None else {}, **limits)

    def test_valid_selected_files_and_ignored_metadata(self):
        data = b'library bytes'
        self.prepare([(self.ROOT + '/README', 'file', b'ignored'),
                      (self.ROOT + '/Release/cef.dll', 'file', data),
                      (self.ROOT + '/Resources/locales/en.pak', 'file', b'locale')],
                     {'Release/cef.dll': self.sha(data)})
        self.assertEqual((self.dest / self.ROOT / 'Release/cef.dll').read_bytes(), data)
        self.assertFalse((self.dest / self.ROOT / 'README').exists())

    def test_hash_and_extraction_use_same_open_file(self):
        self.build([(self.ROOT + '/Release/a', 'file', b'a')])
        expected_hash = archive_module.digest(self.archive)
        real_digest = archive_module.digest_stream
        real_tar_open = archive_module.tarfile.open
        hashed = []

        def record_hash(stream):
            hashed.append(stream)
            return real_digest(stream)

        def record_extract(*args, **kwargs):
            self.assertEqual(len(hashed), 1)
            self.assertIs(kwargs.get('fileobj'), hashed[0])
            self.assertFalse(hashed[0].closed)
            return real_tar_open(*args, **kwargs)

        with mock.patch.object(archive_module, 'digest_stream', side_effect=record_hash), \
                mock.patch.object(archive_module.tarfile, 'open', side_effect=record_extract):
            archive_module.prepare(self.archive, self.dest, expected_hash, self.ROOT, {})
        self.assertTrue(hashed[0].closed)

    def test_rejects_archive_hash_before_creating_destination(self):
        with self.assertRaisesRegex(ValueError, 'hash mismatch'):
            self.prepare([], archive_hash='0' * 64)
        self.assertFalse(self.dest.exists())

    def test_rejects_existing_destination_without_modifying_it(self):
        self.dest.mkdir()
        marker = self.dest / 'keep'
        marker.write_bytes(b'unchanged')
        with self.assertRaises(FileExistsError):
            self.prepare([])
        self.assertEqual(marker.read_bytes(), b'unchanged')

    def test_missing_required_file(self):
        with self.assertRaisesRegex(ValueError, 'missing or mismatched'):
            self.prepare([], {'Release/libcef.dll': self.sha(b'library')})

    def test_required_file_hash_mismatch(self):
        with self.assertRaisesRegex(ValueError, 'missing or mismatched'):
            self.prepare([(self.ROOT + '/Release/libcef.dll', 'file', b'wrong')],
                         {'Release/libcef.dll': self.sha(b'expected')})

    def test_duplicate_and_case_collision(self):
        for second in ('Release/a', 'release/A'):
            with self.subTest(second=second), tempfile.TemporaryDirectory() as d:
                self.dest = Path(d) / 'out'
                with self.assertRaisesRegex(ValueError, 'duplicate'):
                    self.prepare([(self.ROOT + '/Release/a', 'file', b'a'),
                                  (self.ROOT + '/' + second, 'file', b'b')])

    def test_unsafe_paths(self):
        for name in ('/absolute', '../escape', self.ROOT + '/../escape',
                     self.ROOT + '/Release/../../escape',
                     self.ROOT + '/Release/C:escape',
                     self.ROOT + '/Release/a\\b',
                     self.ROOT + '/Release/CON.txt',
                     self.ROOT + '/Release/name.',
                     self.ROOT + '/Release/name ',
                     self.ROOT + '/Release//empty',
                     'wrong-root/Release/file'):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as d:
                self.dest = Path(d) / 'out'
                with self.assertRaisesRegex(ValueError, 'unsafe'):
                    self.prepare([(name, 'file', b'x')])

    def test_symlink_and_hardlink_rejected(self):
        for kind in ('symlink', 'hardlink'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as d:
                self.dest = Path(d) / 'out'
                with self.assertRaisesRegex(ValueError, 'unsupported'):
                    self.prepare([(self.ROOT + '/Release/link', kind, '../outside')])

    def test_max_member_count(self):
        with self.assertRaisesRegex(ValueError, 'entry limit'):
            self.prepare([(self.ROOT + '/a', 'file', b'a'),
                          (self.ROOT + '/b', 'file', b'b')], max_members=1)

    def test_max_file_size(self):
        with self.assertRaisesRegex(ValueError, 'member size'):
            self.prepare([(self.ROOT + '/Release/a', 'file', b'123')], max_file=2)

    def test_max_total_size_includes_unselected_files(self):
        with self.assertRaisesRegex(ValueError, 'archive size'):
            self.prepare([(self.ROOT + '/metadata', 'file', b'12'),
                          (self.ROOT + '/Release/a', 'file', b'34')], max_total=3)

    def test_truncated_archive_not_accepted_even_with_matching_test_digest(self):
        self.build([(self.ROOT + '/Release/a', 'file', b'a' * 10000)])
        data = self.archive.read_bytes()
        self.archive.write_bytes(data[:len(data) // 2])
        with self.assertRaises((tarfile.TarError, EOFError, OSError)):
            archive_module.prepare(self.archive, self.dest,
                                   archive_module.digest(self.archive), self.ROOT, {})

    def test_partial_preparation_is_not_reused(self):
        entries = [(self.ROOT + '/Release/a', 'file', b'a'),
                   (self.ROOT + '/../escape', 'file', b'b')]
        with self.assertRaisesRegex(ValueError, 'unsafe'):
            self.prepare(entries)
        self.assertTrue((self.dest / self.ROOT / 'Release/a').exists())
        with self.assertRaises(FileExistsError):
            self.prepare([(self.ROOT + '/Release/a', 'file', b'a')])


if __name__ == '__main__':
    unittest.main(verbosity=2)
