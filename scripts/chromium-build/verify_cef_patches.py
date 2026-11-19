#!/usr/bin/env python3
"""Fail-closed CEF patch provenance verifier; research build preparation only.

The pinned CEF patcher can target multiple independent repositories. This
implementation reconstructs a single root only and refuses multi-root inputs;
it cannot yet authorize the full pinned CEF checkout.
"""
import argparse
import ast
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
import tempfile

CEF_COMMIT = '14c5a089e8452874cb1a556dc3fde26ed2d87723'
CHROMIUM_COMMIT = 'b859317bf11f6be47f9b7799ec690a0a42a1fb33'
CEF_ORIGIN = 'https://github.com/chromiumembedded/cef.git'
CHROMIUM_ORIGIN = 'https://chromium.googlesource.com/chromium/src.git'
DEPOT_ORIGIN = 'https://chromium.googlesource.com/chromium/tools/depot_tools.git'
NAME = re.compile(r'^[A-Za-z0-9_-]+$')
COND = re.compile(r'^[A-Za-z_][A-Za-z0-9_]*$')
SHA = re.compile(r'^[0-9a-f]{40}$')


class VerifyError(Exception):
    def __init__(self, code, status='FAILED', details=None):
        self.code, self.status, self.details = code, status, details or {}
        super().__init__(code)


def git_environment(index=None):
    # Inherited Git redirection/config variables must not make a disposable
    # reconstruction write into an unrelated repository or run custom helpers.
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith('GIT_')}
    env['GIT_CONFIG_NOSYSTEM'] = '1'
    env['GIT_CONFIG_GLOBAL'] = os.devnull
    env['GIT_TERMINAL_PROMPT'] = '0'
    env['GIT_OPTIONAL_LOCKS'] = '0'
    if index is not None:
        env['GIT_INDEX_FILE'] = str(index)
    return env


def run_git(repo, args, *, data=None, env=None, check=True):
    command = ['git', '-c', 'core.fsmonitor=false', '-c', 'core.hooksPath=',
               '-c', 'diff.external=', '-C', str(repo)] + list(args)
    result = subprocess.run(command, input=data, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, env=env or git_environment(), timeout=600)
    if check and result.returncode:
        raise VerifyError('GIT_OPERATION_FAILED')
    return result


def checkout_identity(repo, revision, origin):
    path = Path(repo).resolve(strict=True)
    top = run_git(path, ['rev-parse', '--show-toplevel']).stdout.decode().strip()
    if Path(top).resolve() != path:
        raise VerifyError('REPOSITORY_ROOT_MISMATCH')
    head = run_git(path, ['rev-parse', 'HEAD']).stdout.decode().strip()
    remote = run_git(path, ['remote', 'get-url', 'origin']).stdout.decode().strip()
    if head != revision:
        raise VerifyError('REVISION_MISMATCH')
    if remote != origin:
        raise VerifyError('ORIGIN_MISMATCH')
    return path


def clean_repository(repo):
    status = run_git(repo, ['status', '--porcelain=v1', '--untracked-files=all']).stdout
    if status:
        raise VerifyError('TRUSTED_REPOSITORY_NOT_CLEAN')


def committed_file(repo, name):
    p = Path(repo) / Path(*PurePosixPath(name).parts)
    if p.is_symlink() or not p.is_file():
        raise VerifyError('PINNED_FILE_MISSING_OR_SYMLINK')
    expected = run_git(repo, ['show', 'HEAD:' + name]).stdout
    if p.read_bytes() != expected:
        raise VerifyError('PINNED_FILE_MISMATCH')
    return expected


def parse_config(data):
    try:
        module = ast.parse(data.decode('utf-8'))
        if len(module.body) != 1 or not isinstance(module.body[0], ast.Assign):
            raise ValueError()
        item = module.body[0]
        if len(item.targets) != 1 or not isinstance(item.targets[0], ast.Name) or item.targets[0].id != 'patches':
            raise ValueError()
        # Reject duplicate dictionary keys instead of silently accepting the last.
        if not isinstance(item.value, ast.List):
            raise ValueError()
        for node in item.value.elts:
            if not isinstance(node, ast.Dict):
                raise ValueError()
            keys = [ast.literal_eval(key) for key in node.keys]
            if len(keys) != len(set(keys)):
                raise ValueError()
        patches = ast.literal_eval(item.value)
    except (ValueError, TypeError, SyntaxError, UnicodeError):
        raise VerifyError('UNSUPPORTED_PATCH_CONFIG')
    if not patches:
        raise VerifyError('EMPTY_PATCH_CONFIG')
    seen = set()
    for entry in patches:
        if not isinstance(entry, dict) or set(entry) - {'name', 'path', 'condition', 'note'}:
            raise VerifyError('UNSUPPORTED_PATCH_ENTRY')
        name = entry.get('name')
        if not isinstance(name, str) or not NAME.fullmatch(name) or name in seen:
            raise VerifyError('INVALID_OR_DUPLICATE_PATCH_NAME')
        seen.add(name)
        target = entry.get('path', '')
        if not isinstance(target, str) or '\\' in target or ':' in target:
            raise VerifyError('INVALID_PATCH_TARGET')
        if target and (target.startswith('/') or any(p in ('', '.', '..') for p in target.split('/'))):
            raise VerifyError('INVALID_PATCH_TARGET')
        condition = entry.get('condition')
        if condition is not None and (not isinstance(condition, str) or not COND.fullmatch(condition)):
            raise VerifyError('INVALID_PATCH_CONDITION')
        if 'note' in entry and not isinstance(entry['note'], str):
            raise VerifyError('INVALID_PATCH_NOTE')
    return patches


def patch_plan(cef, config, environment):
    plan = []
    for entry in parse_config(config):
        name = entry['name']
        contents = committed_file(cef, 'patch/patches/' + name + '.patch')
        condition = entry.get('condition')
        enabled = condition is None or condition in environment
        plan.append({'name': name, 'target': entry.get('path', ''),
                     'condition': condition, 'enabled': enabled,
                     'sha256': hashlib.sha256(contents).hexdigest(), 'bytes': contents})
    return plan


def reconstruct_root(repo, commit, patches):
    # Clone shares read-only source objects but has its own index, refs and new
    # objects. Never apply a patch or write an index in the prepared checkout.
    flags = run_git(repo, ['ls-files', '-v', '-z']).stdout.split(b'\0')
    if any(v and (v[:1] == b'S' or v[:1].islower()) for v in flags):
        raise VerifyError('HIDDEN_INDEX_FLAGS')
    extra = run_git(repo, ['ls-files', '--others', '--exclude-standard', '-z']).stdout
    if extra:
        raise VerifyError('UNTRACKED_FILES_PRESENT')
    # Ignored files may include build inputs; an allowlist with proven scope is
    # not available yet. Do not silently disregard them.
    ignored = run_git(repo, ['ls-files', '--others', '--ignored', '--exclude-standard', '-z']).stdout
    if ignored:
        raise VerifyError('IGNORED_FILES_REQUIRE_REVIEW', 'INCONCLUSIVE')
    diff = run_git(repo, ['diff', '--binary', '--no-ext-diff', '--no-textconv', 'HEAD', '--']).stdout
    with tempfile.TemporaryDirectory(prefix='aegis-cef-verify-') as temp:
        clone = Path(temp) / 'clone'
        result = subprocess.run(['git', 'clone', '--shared', '--no-checkout', '--', str(repo), str(clone)],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=git_environment(), timeout=600)
        if result.returncode:
            raise VerifyError('ISOLATED_CLONE_FAILED')
        run_git(clone, ['read-tree', commit])
        for patch in patches:
            contents = patch['bytes'].replace(b'\r\n', b'\n')
            run_git(clone, ['apply', '--cached', '-p0', '--ignore-whitespace', '--check', '-'], data=contents)
            run_git(clone, ['apply', '--cached', '-p0', '--ignore-whitespace', '-'], data=contents)
        expected = run_git(clone, ['write-tree']).stdout.decode().strip()
        env = git_environment(Path(temp) / 'actual.index')
        run_git(clone, ['read-tree', commit], env=env)
        if diff:
            run_git(clone, ['apply', '--cached', '--binary', '-'], data=diff, env=env)
        actual = run_git(clone, ['write-tree'], env=env).stdout.decode().strip()
        # Catch ordinary concurrent tracked modifications, rather than reuse an
        # earlier report after the source changed during reconstruction.
        after = run_git(repo, ['diff', '--binary', '--no-ext-diff', '--no-textconv', 'HEAD', '--']).stdout
        if diff != after:
            raise VerifyError('CHECKOUT_CHANGED_DURING_VERIFICATION')
        if expected != actual:
            raise VerifyError('PATCHED_TREE_MISMATCH')
        return expected, actual


def verify(chromium, cef, depot, depot_commit):
    if not SHA.fullmatch(depot_commit):
        raise VerifyError('INVALID_DEPOT_REVISION')
    chromium = checkout_identity(chromium, CHROMIUM_COMMIT, CHROMIUM_ORIGIN)
    cef = checkout_identity(cef, CEF_COMMIT, CEF_ORIGIN)
    depot = checkout_identity(depot, depot_commit, DEPOT_ORIGIN)
    clean_repository(cef)
    clean_repository(depot)
    config = committed_file(cef, 'patch/patch.cfg')
    metadata = committed_file(cef, 'CHROMIUM_BUILD_COMPATIBILITY.txt')
    try:
        compatibility = ast.literal_eval(metadata.decode('utf-8'))
    except (ValueError, SyntaxError, UnicodeError):
        raise VerifyError('COMPATIBILITY_METADATA_INVALID')
    if compatibility.get('chromium_checkout') != 'refs/tags/154.0.8037.98':
        raise VerifyError('COMPATIBILITY_MISMATCH')
    plan = patch_plan(cef, config, os.environ)
    evidence = {'cef_commit': CEF_COMMIT, 'chromium_commit': CHROMIUM_COMMIT,
                'depot_tools_commit': depot_commit, 'cef_branch': '8037',
                'chromium_tag': '154.0.8037.98', 'config_sha256': hashlib.sha256(config).hexdigest(),
                'patches': [{k: v for k, v in p.items() if k != 'bytes'} for p in plan]}
    # Pinned CEF includes nested git repos. Their expected base revisions and
    # generated files have not yet been independently established. Refuse to
    # authorize rather than verify only the Chromium root and call it complete.
    if any(p['enabled'] and p['target'] for p in plan):
        raise VerifyError('MULTI_REPOSITORY_PROVENANCE_UNSUPPORTED', 'INCONCLUSIVE', evidence)
    expected, actual = reconstruct_root(chromium, CHROMIUM_COMMIT, [p for p in plan if p['enabled']])
    return dict(evidence, schema='aegis-cef-patch-provenance-v1', status='VERIFIED',
                expected_tree=expected, actual_tree=actual)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    for arg in ('chromium-source', 'cef-source', 'depot-tools', 'depot-tools-commit', 'report'):
        parser.add_argument('--' + arg, required=True)
    args = parser.parse_args(argv)
    report_path = Path(args.report)
    # Evidence must be written exclusively, outside all checkout trees.
    target = report_path.resolve(strict=False)
    for root in (args.chromium_source, args.cef_source, args.depot_tools):
        resolved = Path(root).resolve(strict=False)
        if target == resolved or resolved in target.parents:
            print('REPORT_INSIDE_CHECKOUT', file=sys.stderr)
            return 1
    try:
        report = verify(args.chromium_source, args.cef_source, args.depot_tools, args.depot_tools_commit)
    except (VerifyError, OSError, subprocess.SubprocessError) as exc:
        report = {'schema': 'aegis-cef-patch-provenance-v1',
                  'status': exc.status if isinstance(exc, VerifyError) else 'FAILED',
                  'error': exc.code if isinstance(exc, VerifyError) else 'IO_OR_PROCESS_ERROR'}
        if isinstance(exc, VerifyError):
            report.update(exc.details)
    try:
        with report_path.open('x', encoding='utf-8') as stream:
            json.dump(report, stream, sort_keys=True, indent=2)
            stream.write('\n')
    except OSError:
        print('REPORT_WRITE_FAILED', file=sys.stderr)
        return 1
    print(json.dumps(report, sort_keys=True))
    return 0 if report['status'] == 'VERIFIED' else 1


if __name__ == '__main__':
    sys.exit(main())
