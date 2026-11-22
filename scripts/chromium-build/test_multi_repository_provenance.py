"""Synthetic Git repositories; never substitutes for a Chromium checkout."""
import importlib.util
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

spec = importlib.util.spec_from_file_location('verifier', Path(__file__).with_name('verify_cef_patches.py'))
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)


def git(path, *args):
    return subprocess.run(['git', '-C', str(path), *args], check=True,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout


def init(path):
    path.mkdir(parents=True)
    git(path, 'init', '-q')
    git(path, 'config', 'user.name', 'Synthetic fixture')
    git(path, 'config', 'user.email', 'test@example.invalid')
    git(path, 'config', 'core.autocrlf', 'false')


class MultipleRepositories(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='aegis-multirepo-')
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name) / 'chromium'
        init(self.root)
        self.targets = ['v8', 'third_party/depot_tools', 'third_party/angle',
                        'third_party/dawn', 'third_party/devtools-frontend/src']
        self.rows, self.plan = [], []
        for index, target in enumerate(self.targets):
            repo = self.root.joinpath(*target.split('/'))
            init(repo)
            origin = 'https://chromium.googlesource.com/fixture/repo' + str(index)
            git(repo, 'remote', 'add', 'origin', origin)
            file = repo / 'source.txt'
            file.write_bytes(b'original\n')
            git(repo, 'add', 'source.txt')
            git(repo, 'commit', '-qm', 'base')
            rev = git(repo, 'rev-parse', 'HEAD').decode().strip()
            self.rows.append((target, origin, rev))
            file.write_bytes(b'changed\n')
            patch = git(repo, 'diff', '--binary', '--no-prefix', 'HEAD', '--')
            self.plan.append({'name': 'p' + str(index), 'target': target,
                              'enabled': True, 'bytes': patch})
        self.write_deps()

    def write_deps(self):
        rows = ',\n'.join(repr('src/' + target) + ': ' + repr(origin + '@' + rev)
                          for target, origin, rev in self.rows)
        (self.root / 'DEPS').write_text('vars = {}\ndeps = {\n' + rows + '\n}\n', encoding='utf-8')
        git(self.root, 'add', 'DEPS')
        git(self.root, 'commit', '-qm', 'pinned synthetic deps')

    def test_five_repository_candidates_match_but_full_status_stays_inconclusive(self):
        result = verifier.reconstruct_direct_dependencies(self.root, self.plan)
        self.assertEqual(result['status'], 'INCONCLUSIVE')
        self.assertEqual([v['path'] for v in result['repositories']], self.targets)
        self.assertTrue(all(v['actual_tree'] == v['expected_tree'] for v in result['repositories']))

    def test_dependency_tamper_is_rejected(self):
        (self.root / 'third_party' / 'angle' / 'source.txt').write_bytes(b'tampered\n')
        with self.assertRaises(verifier.VerifyError):
            verifier.reconstruct_direct_dependencies(self.root, self.plan)

    def test_wrong_commit_is_rejected(self):
        (self.root / 'v8' / 'source.txt').write_bytes(b'different\n')
        git(self.root / 'v8', 'add', 'source.txt')
        git(self.root / 'v8', 'commit', '-qm', 'unexpected')
        with self.assertRaises(verifier.VerifyError):
            verifier.reconstruct_direct_dependencies(self.root, self.plan)

    def test_missing_dependency_is_inconclusive(self):
        (self.root / 'v8').rename(self.root / 'v8_fixture_moved')
        with self.assertRaises(verifier.VerifyError) as error:
            verifier.reconstruct_direct_dependencies(self.root, self.plan)
        self.assertEqual(error.exception.status, 'INCONCLUSIVE')
        self.assertEqual(error.exception.code, 'DEPENDENCY_CHECKOUT_MISSING')

    def test_invalid_target_and_deps_mutation_are_rejected(self):
        with self.assertRaises(verifier.VerifyError):
            verifier.resolve_direct_deps_from_committed_file(self.root, ['../outside'])
        (self.root / 'DEPS').write_text("vars = {}\ndeps = {}\ndeps.update({})\n", encoding='utf-8')
        git(self.root, 'add', 'DEPS')
        git(self.root, 'commit', '-qm', 'unsupported mutation')
        with self.assertRaises(verifier.VerifyError):
            verifier.resolve_direct_deps_from_committed_file(self.root, ['v8'])

    def test_alternate_objects_are_rejected(self):
        path = self.root / 'v8' / '.git' / 'objects' / 'info' / 'alternates'
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text('untrusted-store\n', encoding='utf-8')
        with self.assertRaises(verifier.VerifyError):
            verifier.dependency_repo_path(self.root, 'v8')

    def test_wrong_repository_patch_rejected(self):
        wrong = [dict(value) for value in self.plan]
        wrong[0]['bytes'] = b'not a patch\n'
        with self.assertRaises(verifier.VerifyError):
            verifier.reconstruct_direct_dependencies(self.root, wrong)

    def test_full_verifier_never_accepts_partial_dependency_evidence(self):
        plan = [dict(value) for value in self.plan]
        for entry in plan:
            entry.update(condition=None, sha256='a' * 64)
        metadata = b"{'chromium_checkout':'refs/tags/154.0.8037.98'}"
        def pinned_file(repo, path):
            return metadata if path == 'CHROMIUM_BUILD_COMPATIBILITY.txt' else b'config'
        comparisons = {'status': 'INCONCLUSIVE', 'repositories': []}
        with mock.patch.object(verifier, 'checkout_identity', return_value=self.root), \
             mock.patch.object(verifier, 'clean_repository'), \
             mock.patch.object(verifier, 'committed_file', side_effect=pinned_file), \
             mock.patch.object(verifier, 'patch_plan', return_value=plan), \
             mock.patch.object(verifier, 'reconstruct_direct_dependencies', return_value=comparisons):
            with self.assertRaises(verifier.VerifyError) as error:
                verifier.verify(self.root, self.root, self.root, 'a' * 40)
        self.assertEqual(error.exception.status, 'INCONCLUSIVE')
        self.assertEqual(error.exception.details['dependency_comparisons'], comparisons)


if __name__ == '__main__':
    unittest.main(verbosity=2)
