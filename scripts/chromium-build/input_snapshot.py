#!/usr/bin/env python3
"""Compare complete directory snapshots; does not authorize a CEF build.

Snapshots are observational evidence, not an atomic filesystem freeze or an
independently authenticated source manifest. Every path under the specified
root is considered, including ignored and generated files. No links, junctions,
special files or omissions are accepted.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import sys


class SnapshotError(Exception):
    pass


def _unsafe(info):
    return bool(getattr(info, 'st_file_attributes', 0) & 0x400) or stat.S_ISLNK(info.st_mode)


def _identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_size,
            info.st_mtime_ns, info.st_ctime_ns)


def _same_object(first, second):
    # Windows directory metadata can lag a just-closed write. Compare the
    # object identities at the path boundary and use the opened handle for
    # before/after metadata. A missing file ID cannot establish association.
    return bool(first.st_ino and second.st_ino and first.st_dev == second.st_dev
                and first.st_ino == second.st_ino and first.st_mode == second.st_mode)


def capture(root, *, limit=1_000_000):
    root = Path(root).absolute()
    for ancestor in (root, *root.parents):
        if _unsafe(ancestor.lstat()):
            raise SnapshotError('ROOT_ANCESTOR_LINK_OR_REPARSE_POINT')
    info = root.lstat()
    if _unsafe(info) or not stat.S_ISDIR(info.st_mode):
        raise SnapshotError('ROOT_NOT_PLAIN_DIRECTORY')
    entries = []
    names = set()

    def visit(path, relative):
        before = path.lstat()
        if _unsafe(before):
            raise SnapshotError('LINK_OR_REPARSE_POINT')
        name = relative.as_posix()
        folded = name.casefold()
        if folded in names:
            raise SnapshotError('CASE_COLLISION')
        names.add(folded)
        if len(names) > limit:
            raise SnapshotError('ENTRY_LIMIT_EXCEEDED')
        if stat.S_ISDIR(before.st_mode):
            children = sorted(os.listdir(path))
            entries.append({'path': name, 'kind': 'directory', 'mode': stat.S_IMODE(before.st_mode)})
            for child in children:
                visit(path / child, relative / child)
            if sorted(os.listdir(path)) != children or _identity(path.lstat()) != _identity(before):
                raise SnapshotError('DIRECTORY_CHANGED_DURING_SNAPSHOT')
        elif stat.S_ISREG(before.st_mode):
            digest = hashlib.sha256()
            with path.open('rb') as stream:
                opened = os.fstat(stream.fileno())
                if not _same_object(opened, before):
                    raise SnapshotError('FILE_CHANGED_DURING_SNAPSHOT')
                while True:
                    chunk = stream.read(1024 * 1024)
                    if not chunk:
                        break
                    digest.update(chunk)
                after = os.fstat(stream.fileno())
            if _identity(after) != _identity(opened) or not _same_object(path.lstat(), opened):
                raise SnapshotError('FILE_CHANGED_DURING_SNAPSHOT')
            entries.append({'path': name, 'kind': 'file', 'mode': stat.S_IMODE(before.st_mode),
                            'size': opened.st_size, 'sha256': digest.hexdigest()})
        else:
            raise SnapshotError('SPECIAL_FILE_UNSUPPORTED')

    children = sorted(os.listdir(root))
    for child in children:
        visit(root / child, Path(child))
    if sorted(os.listdir(root)) != children or _identity(root.lstat()) != _identity(info):
        raise SnapshotError('ROOT_CHANGED_DURING_SNAPSHOT')
    encoded = json.dumps(entries, ensure_ascii=True, sort_keys=True, separators=(',', ':')).encode('utf-8')
    return {'schema': 'aegis-input-snapshot-v1', 'status': 'INCONCLUSIVE',
            'entries': len(entries), 'content_sha256': hashlib.sha256(encoded).hexdigest(),
            'compiler_input_provenance': 'NOT_ESTABLISHED'}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True)
    parser.add_argument('--compare-sha256', help='Compare against an independently retained prior digest')
    args = parser.parse_args(argv)
    try:
        result = capture(args.root)
        if args.compare_sha256 is not None:
            if len(args.compare_sha256) != 64 or any(c not in '0123456789abcdef' for c in args.compare_sha256):
                raise SnapshotError('INVALID_COMPARISON_DIGEST')
            result['comparison'] = 'MATCH' if result['content_sha256'] == args.compare_sha256 else 'MISMATCH'
        print(json.dumps(result, sort_keys=True))
        return 1 if result.get('comparison') == 'MISMATCH' else 0
    except (OSError, SnapshotError) as exc:
        print(json.dumps({'schema': 'aegis-input-snapshot-v1', 'status': 'FAILED',
                          'error': str(exc) if isinstance(exc, SnapshotError) else 'IO_ERROR'}), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
