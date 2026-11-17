"""One-pass, fail-closed preparation of the *pinned* research CEF bundle.

This is not a general purpose archive extractor.  Inputs and output stay in an
isolated runner-owned directory; an incomplete destination is never reusable.
"""
import hashlib
import os
from pathlib import Path
import re
import sys
import tarfile
import time

ROOT = 'cef_binary_154.0.34+g14c5a08+chromium-154.0.8037.98_windows64_minimal'
ARCHIVE_SHA256 = 'c81c34d048d87276f4a18f0b000206710a1709bb67dd658948b87100e1a538da'
EXPECTED = {
    'Release/bootstrapc.exe': 'bd0d6dcf683171305c02bcee6b6f5dad2abdd2f90ff8ef0593be585e78e164c9',
    'Release/libcef.dll': 'e3da6707a840a8f33f565e011542169fd51aaa5218603eb7a89039fb16d79ae7',
    'Release/chrome_elf.dll': 'f26656bfe38e1b78e42f292b6a8be1b5f0b5db15395ac571d6e20a1dedf698fb',
}
MAX_MEMBERS = 3000
MAX_TOTAL = 4 * 1024**3
MAX_FILE = 1024**3
_DEVICE = re.compile(r'^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)', re.I)


def digest_stream(stream):
    h = hashlib.sha256()
    for chunk in iter(lambda: stream.read(1024 * 1024), b''):
        h.update(chunk)
    return h.hexdigest()


def digest(path):
    with open(path, 'rb') as stream:
        return digest_stream(stream)


def parts_for(name, root):
    # The pinned tar uses POSIX paths. Reject Windows aliases, streams and
    # ambiguous normalized names, even on a non-Windows test host.
    if not name or name.startswith('/') or '\\' in name or ':' in name:
        raise ValueError('unsafe archive path')
    parts = name.rstrip('/').split('/')
    if not parts or parts[0] != root or any(
        not p or p in ('.', '..') or p[-1:] in (' ', '.') or _DEVICE.match(p)
        or any(ord(ch) < 32 or ch in '<>"|?*' for ch in p)
        for p in parts
    ):
        raise ValueError('unsafe archive path')
    return parts


def prepare(archive, destination, expected_archive, root, expected_files,
            max_members=MAX_MEMBERS, max_total=MAX_TOTAL, max_file=MAX_FILE):
    started = time.monotonic()
    # Hash and decompress the same opened file, so swapping the pathname
    # between those stages cannot substitute another archive. This is not a
    # defense against a hostile process modifying that open file in place;
    # the CI directory and archive must be owned by the trusted runner.
    with open(archive, 'rb') as source:
        if digest_stream(source) != expected_archive.lower():
            raise ValueError('archive hash mismatch')
        source.seek(0)
        _extract_verified(source, destination, root, expected_files, started,
                          max_members, max_total, max_file)


def _extract_verified(source, destination, root, expected_files, started,
                      max_members, max_total, max_file):
    print('ARCHIVE_HASH_VERIFIED seconds=%.3f' % (time.monotonic() - started), flush=True)
    # mkdir is exclusive. Never repair or reuse a partially prepared tree.
    destination = Path(destination)
    destination.mkdir(parents=False, exist_ok=False)
    seen = set()
    count = total = selected = 0
    start_extract = time.monotonic()
    with tarfile.open(fileobj=source, mode='r|bz2') as tar:
        for entry in tar:
            count += 1
            if count > max_members:
                raise ValueError('archive entry limit exceeded')
            parts = parts_for(entry.name, root)
            key = '/'.join(parts).casefold()
            if key in seen:
                raise ValueError('duplicate or conflicting entry')
            seen.add(key)
            if not (entry.isdir() or entry.isfile()):
                raise ValueError('unsupported archive entry type')
            if entry.size < 0 or entry.size > max_file:
                raise ValueError('archive member size limit exceeded')
            total += entry.size
            if total > max_total:
                raise ValueError('archive size limit exceeded')
            if len(parts) < 2 or parts[1] not in ('Release', 'Resources'):
                continue
            relative = Path(*parts)
            target = destination / relative
            if entry.isdir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                member_stream = tar.extractfile(entry)
                if member_stream is None:
                    raise ValueError('file data unavailable')
                remaining = entry.size
                with member_stream, open(target, 'xb') as output:
                    while remaining:
                        chunk = member_stream.read(min(1024 * 1024, remaining))
                        if not chunk:
                            raise ValueError('truncated member')
                        output.write(chunk)
                        remaining -= len(chunk)
                selected += 1
    print('ARCHIVE_EXTRACTION_COMPLETE entries=%d selected=%d bytes=%d seconds=%.3f' %
          (count, selected, total, time.monotonic() - start_extract), flush=True)
    for relative, expected in expected_files.items():
        file = destination / root / Path(relative)
        if not file.is_file() or digest(file) != expected.lower():
            raise ValueError('required native dependency missing or mismatched: ' + relative)
    print('EXTRACTED_FILES_VERIFIED count=%d seconds=%.3f' %
          (len(expected_files), time.monotonic() - started), flush=True)


def main():
    if len(sys.argv) != 3:
        print('usage: prepare_cef_archive.py ARCHIVE NONEXISTENT_DESTINATION', file=sys.stderr)
        return 2
    try:
        prepare(sys.argv[1], sys.argv[2], ARCHIVE_SHA256, ROOT, EXPECTED)
    except (OSError, ValueError, tarfile.TarError, EOFError) as exc:
        print('ARCHIVE_PREPARATION_FAILED: ' + str(exc), file=sys.stderr, flush=True)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
