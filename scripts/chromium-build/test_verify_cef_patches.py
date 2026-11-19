"""Synthetic, offline tests; these do not verify a real Chromium checkout."""
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

SCRIPT = Path(__file__).with_name('verify_cef_patches.py')
spec = importlib.util.spec_from_file_location('verify_cef_patches', SCRIPT)
subject = importlib.util.module_from_spec(spec)
spec.loader.exec_module(subject)


def git(repo, *arguments):
    result = subprocess.run(['git', '-C', str(repo), *arguments], check=True,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return result.stdout


class ConfigurationTests(unittest.TestCase):
    def test_literal_config_and_patch_order(self):
        value = subject.parse_config(b"patches = [{'name':'first'}, {'name':'second','path':'v8'}]")
        self.assertEqual([p['name'] for p in value], ['first', 'second'])
        self.assertEqual(value[1]['path'], 'v8')

    def test_rejects_executable_and_unsupported_config(self):
        for value in (b"import os\npatches = []", b"patches = make_patches()",
                      b"patches = [{'name':'a','unknown':'x'}]",
                      b"patches = [{'name':'a','name':'b'}]",
                      b"patches = [{'name':'a'}, {'name':'a'}]", b'patches = []'):
            with self.subTest(value=value), self.assertRaises(subject.VerifyError):
                subject.parse_config(value)

    def test_path_traversal_and_malformed_conditions_rejected(self):
        for value in ("../escape", "/absolute", "v8/../escape", "v8//other", "v8\\other", "C:other"):
            with self.subTest(value=value), self.assertRaises(subject.VerifyError):
                subject.parse_config(("patches = [{'name':'a','path':" + repr(value) + "}]").encode())
        for value in ('../a', 'a.patch', 'a/b', ''):
            with self.subTest(value=value), self.assertRaises(subject.VerifyError):
                subject.parse_config(("patches = [{'name':" + repr(value) + "}]").encode())
        with self.assertRaises(subject.VerifyError):
            subject.parse_config(b"patches = [{'name':'a','condition':'A=B'}]")


class RepositoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='aegis-patch-tests-')
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name) / 'repo'
        self.repo.mkdir()
        git(self.repo, 'init', '-q')
        git(self.repo, 'config', 'user.email', 'test@example.invalid')
        git(self.repo, 'config', 'user.name', 'Synthetic Test')
        git(self.repo, 'config', 'core.autocrlf', 'false')
        (self.repo / 'file.txt').write_bytes(b'original\n')
        (self.repo / 'remove.txt').write_bytes(b'remove me\n')
        git(self.repo, 'add', '.')
        git(self.repo, 'commit', '-qm', 'synthetic baseline')
        self.commit = git(self.repo, 'rev-parse', 'HEAD').decode().strip()

    def patch(self, first=b'changed\n', delete=False):
        (self.repo / 'file.txt').write_bytes(first)
        if delete:
            (self.repo / 'remove.txt').unlink()
        return git(self.repo, 'diff', '--binary', '--no-prefix', 'HEAD', '--')

    @staticmethod
    def patch_record(contents):
        return {'name': 'synthetic', 'bytes': contents}

    def assert_failure(self, expected, operation):
        with self.assertRaises(subject.VerifyError) as caught:
            operation()
        self.assertEqual(caught.exception.code, expected)

    def test_matching_patched_root_reconstructs_tree(self):
        patch = self.patch()
        expected, actual = subject.reconstruct_root(self.repo, self.commit, [self.patch_record(patch)])
        self.assertEqual(expected, actual)

    def test_unexpected_tracked_change_rejected(self):
        patch = self.patch()
        (self.repo / 'file.txt').write_bytes(b'tampered\n')
        self.assert_failure('PATCHED_TREE_MISMATCH', lambda: subject.reconstruct_root(
            self.repo, self.commit, [self.patch_record(patch)]))

    def test_untracked_source_rejected(self):
        patch = self.patch()
        (self.repo / 'extra.cpp').write_bytes(b'extra\n')
        self.assert_failure('UNTRACKED_FILES_PRESENT', lambda: subject.reconstruct_root(
            self.repo, self.commit, [self.patch_record(patch)]))

    def test_ignored_source_is_inconclusive(self):
        (self.repo / '.gitignore').write_text('ignored.obj\n', encoding='utf-8')
        git(self.repo, 'add', '.gitignore')
        git(self.repo, 'commit', '-qm', 'synthetic ignore')
        commit = git(self.repo, 'rev-parse', 'HEAD').decode().strip()
        patch = self.patch()
        (self.repo / 'ignored.obj').write_bytes(b'unknown\n')
        with self.assertRaises(subject.VerifyError) as caught:
            subject.reconstruct_root(self.repo, commit, [self.patch_record(patch)])
        self.assertEqual(caught.exception.code, 'IGNORED_FILES_REQUIRE_REVIEW')
        self.assertEqual(caught.exception.status, 'INCONCLUSIVE')

    def test_unexpected_deletion_rejected(self):
        patch = self.patch()
        (self.repo / 'remove.txt').unlink()
        self.assert_failure('PATCHED_TREE_MISMATCH', lambda: subject.reconstruct_root(
            self.repo, self.commit, [self.patch_record(patch)]))

    def test_missing_expected_deletion_rejected(self):
        patch = self.patch(delete=True)
        (self.repo / 'remove.txt').write_bytes(b'remove me\n')
        self.assert_failure('PATCHED_TREE_MISMATCH', lambda: subject.reconstruct_root(
            self.repo, self.commit, [self.patch_record(patch)]))

    def test_mode_change_rejected(self):
        patch = self.patch()
        git(self.repo, 'update-index', '--chmod=+x', 'remove.txt')
        self.assert_failure('PATCHED_TREE_MISMATCH', lambda: subject.reconstruct_root(
            self.repo, self.commit, [self.patch_record(patch)]))

    def test_patch_order_is_enforced(self):
        first = self.patch(b'first\n')
        (self.repo / 'file.txt').write_bytes(b'second\n')
        # Build the second incremental patch from first to second.
        git(self.repo, 'add', 'file.txt')
        (self.repo / 'file.txt').write_bytes(b'third\n')
        second = git(self.repo, 'diff', '--no-prefix', '--', 'file.txt')
        # This intentionally inconsistent sequence cannot apply from baseline.
        self.assert_failure('GIT_OPERATION_FAILED', lambda: subject.reconstruct_root(
            self.repo, self.commit, [self.patch_record(second), self.patch_record(first)]))

    def test_hidden_index_entry_rejected(self):
        patch = self.patch()
        git(self.repo, 'update-index', '--assume-unchanged', 'remove.txt')
        self.assert_failure('HIDDEN_INDEX_FLAGS', lambda: subject.reconstruct_root(
            self.repo, self.commit, [self.patch_record(patch)]))

    def test_alternate_object_store_is_rejected(self):
        info = self.repo / '.git' / 'objects' / 'info'
        info.mkdir(parents=True, exist_ok=True)
        (info / 'alternates').write_text('untrusted-store\n', encoding='utf-8')
        self.assert_failure('GIT_OBJECT_INDIRECTION_UNSUPPORTED', lambda: subject.reconstruct_root(
            self.repo, self.commit, []))

    def test_replace_ref_is_rejected(self):
        (self.repo / 'file.txt').write_bytes(b'replacement\n')
        git(self.repo, 'add', 'file.txt')
        git(self.repo, 'commit', '-qm', 'replacement')
        replacement = git(self.repo, 'rev-parse', 'HEAD').decode().strip()
        git(self.repo, 'replace', self.commit, replacement)
        self.assert_failure('GIT_REPLACE_REFS_UNSUPPORTED', lambda: subject.reconstruct_root(
            self.repo, self.commit, []))

    def test_configured_clean_filter_is_rejected_before_worktree_diff(self):
        git(self.repo, 'config', 'filter.sentinel.clean', 'should-never-run')
        self.assert_failure('GIT_FILTER_CONFIGURATION_UNSUPPORTED', lambda: subject.reconstruct_root(
            self.repo, self.commit, []))

    def test_git_environment_drops_external_redirection(self):
        with mock.patch.dict(os.environ, {'GIT_DIR': 'external', 'GIT_INDEX_FILE': 'external'}):
            env = subject.git_environment()
        self.assertNotIn('GIT_DIR', env)
        self.assertNotIn('GIT_INDEX_FILE', env)
        self.assertEqual(env['GIT_NO_REPLACE_OBJECTS'], '1')

    def test_partial_patch_rejected(self):
        patch = self.patch() + b'\nthis is not a valid second diff\n'
        # The expected content differs from what was actually checked out.
        (self.repo / 'file.txt').write_bytes(b'incomplete\n')
        with self.assertRaises(subject.VerifyError):
            subject.reconstruct_root(self.repo, self.commit, [self.patch_record(patch)])

    def test_changed_checkout_rejected_after_prior_success(self):
        patch = self.patch()
        subject.reconstruct_root(self.repo, self.commit, [self.patch_record(patch)])
        (self.repo / 'file.txt').write_bytes(b'changed after prior check\n')
        self.assert_failure('PATCHED_TREE_MISMATCH', lambda: subject.reconstruct_root(
            self.repo, self.commit, [self.patch_record(patch)]))

    def test_source_identity_rejects_wrong_revision_and_origin(self):
        git(self.repo, 'remote', 'add', 'origin', 'https://example.invalid/source.git')
        self.assert_failure('REVISION_MISMATCH', lambda: subject.checkout_identity(
            self.repo, '0' * 40, 'https://example.invalid/source.git'))
        self.assert_failure('ORIGIN_MISMATCH', lambda: subject.checkout_identity(
            self.repo, self.commit, 'https://example.invalid/other.git'))

    def test_patch_integrity_and_conditional_presence(self):
        patch_dir = self.repo / 'patch' / 'patches'
        patch_dir.mkdir(parents=True)
        patch_file = patch_dir / 'sample.patch'
        patch_file.write_bytes(b'patch bytes\n')
        git(self.repo, 'add', 'patch/patches/sample.patch')
        git(self.repo, 'commit', '-qm', 'synthetic patch')
        config = b"patches = [{'name':'sample','condition':'ENABLE_SAMPLE'}]"
        enabled = subject.patch_plan(self.repo, config, {'ENABLE_SAMPLE': ''})
        disabled = subject.patch_plan(self.repo, config, {})
        self.assertTrue(enabled[0]['enabled'])
        self.assertFalse(disabled[0]['enabled'])
        self.assertEqual(enabled[0]['sha256'], hashlib.sha256(b'patch bytes\n').hexdigest())
        patch_file.write_bytes(b'tampered\n')
        self.assert_failure('PINNED_FILE_MISMATCH', lambda: subject.patch_plan(self.repo, config, {}))
        patch_file.unlink()
        self.assert_failure('PINNED_FILE_MISSING_OR_SYMLINK', lambda: subject.patch_plan(self.repo, config, {}))

    def test_existing_report_cannot_be_reused(self):
        report = Path(self.temp.name) / 'report.json'
        report.write_text('{"status":"FAILED"}', encoding='utf-8')
        args = ['--chromium-source', str(self.repo), '--cef-source', str(self.repo),
                '--depot-tools', str(self.repo), '--depot-tools-commit', '0' * 40,
                '--report', str(report)]
        with mock.patch.object(subject, 'verify', return_value={'status': 'VERIFIED'}), \
                contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertNotEqual(subject.main(args), 0)
        self.assertEqual(json.loads(report.read_text(encoding='utf-8'))['status'], 'FAILED')

    def test_report_inside_checkout_is_rejected(self):
        report = self.repo / 'report.json'
        args = ['--chromium-source', str(self.repo), '--cef-source', str(self.repo),
                '--depot-tools', str(self.repo), '--depot-tools-commit', '0' * 40,
                '--report', str(report)]
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertNotEqual(subject.main(args), 0)
        self.assertFalse(report.exists())

    def test_inconclusive_result_never_returns_success(self):
        report = Path(self.temp.name) / 'inconclusive.json'
        args = ['--chromium-source', str(self.repo), '--cef-source', str(self.repo),
                '--depot-tools', str(self.repo), '--depot-tools-commit', '0' * 40,
                '--report', str(report)]
        failure = subject.VerifyError('MULTI_REPOSITORY_PROVENANCE_UNSUPPORTED', 'INCONCLUSIVE')
        with mock.patch.object(subject, 'verify', side_effect=failure), \
                contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertNotEqual(subject.main(args), 0)
        evidence = json.loads(report.read_text(encoding='utf-8'))
        self.assertEqual(evidence['status'], 'INCONCLUSIVE')
        self.assertNotIn(str(self.repo), json.dumps(evidence))


if __name__ == '__main__':
    unittest.main(verbosity=2)
