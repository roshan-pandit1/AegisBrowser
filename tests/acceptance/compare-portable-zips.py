#!/usr/bin/env python3
"""Compare two ZIPs without extracting them or executing their contents."""

import argparse
import hashlib
import json
from pathlib import Path
import struct
import zipfile


def sha256_stream(stream):
    digest = hashlib.sha256()
    while chunk := stream.read(1024 * 1024):
        digest.update(chunk)
    return digest.hexdigest()


def raw_entry_metadata(raw, info):
    raw.seek(info.header_offset)
    header = raw.read(30)
    if len(header) != 30:
        raise ValueError("truncated ZIP local header")
    fields = struct.unpack("<IHHHHHIIIHH", header)
    if fields[0] != 0x04034B50:
        raise ValueError("invalid ZIP local header")
    filename = raw.read(fields[9])
    extra = raw.read(fields[10])
    if len(filename) != fields[9] or len(extra) != fields[10]:
        raise ValueError("truncated ZIP local header fields")
    remaining = info.compress_size
    digest = hashlib.sha256()
    while remaining:
        data = raw.read(min(1024 * 1024, remaining))
        if not data:
            raise ValueError("truncated compressed data")
        digest.update(data)
        remaining -= len(data)
    return {
        "local_header_offset": info.header_offset,
        "local_filename": filename.hex(),
        "local_extra": extra.hex(),
        "local_flags": fields[2],
        "local_compression_method": fields[3],
        "local_time": fields[4],
        "local_date": fields[5],
        "local_crc32": fields[6],
        "local_compressed_size": fields[7],
        "local_size": fields[8],
        "compressed_sha256": digest.hexdigest(),
    }


def snapshot(path):
    with path.open("rb") as raw:
        archive_sha256 = sha256_stream(raw)
    with zipfile.ZipFile(path) as archive, path.open("rb") as raw:
        entries = {}
        order = []
        central_offset = archive.start_dir
        timestamp_offsets = []
        for info in archive.infolist():
            if info.filename in entries:
                raise ValueError(f"duplicate ZIP entry: {info.filename}")
            order.append(info.filename)
            with archive.open(info) as entry:
                content_sha256 = sha256_stream(entry)
            raw.seek(central_offset)
            central_header = raw.read(46)
            if len(central_header) != 46 or central_header[:4] != b"PK\x01\x02":
                raise ValueError("invalid ZIP central-directory header")
            filename_size, extra_size, comment_size = struct.unpack_from("<HHH", central_header, 28)
            timestamp_offsets.extend(range(info.header_offset + 10, info.header_offset + 14))
            timestamp_offsets.extend(range(central_offset + 12, central_offset + 16))
            entries[info.filename] = {
                "directory": info.is_dir(),
                "sha256": content_sha256,
                "size": info.file_size,
                "compressed_size": info.compress_size,
                "crc32": f"{info.CRC:08x}",
                "compression_method": info.compress_type,
                "timestamp": list(info.date_time),
                "creator_system": info.create_system,
                "creator_version": info.create_version,
                "extract_version": info.extract_version,
                "flags": info.flag_bits,
                "internal_attributes": info.internal_attr,
                "external_attributes": info.external_attr,
                "extra": info.extra.hex(),
                "comment": info.comment.hex(),
                "central_header_offset": central_offset,
                **raw_entry_metadata(raw, info),
            }
            central_offset += 46 + filename_size + extra_size + comment_size
        return {
            "archive_sha256": archive_sha256,
            "archive_size": path.stat().st_size,
            "archive_comment": archive.comment.hex(),
            "order": order,
            "entries": entries,
            "timestamp_offsets": timestamp_offsets,
        }


def raw_byte_comparison(path_a, path_b, allowed):
    different = 0
    unexpected = []
    offset = 0
    same_size = path_a.stat().st_size == path_b.stat().st_size
    with path_a.open("rb") as fa, path_b.open("rb") as fb:
        while True:
            left = fa.read(1024 * 1024)
            right = fb.read(1024 * 1024)
            if not left and not right:
                break
            if left != right:
                for index in range(max(len(left), len(right))):
                    if index >= len(left) or index >= len(right) or left[index] != right[index]:
                        location = offset + index
                        different += 1
                        if location not in allowed and len(unexpected) < 20:
                            unexpected.append(location)
            offset += max(len(left), len(right))
    return {
        "same_size": same_size,
        "different_bytes": different,
        "unexpected_offsets_first_20": unexpected,
        "only_timestamp_header_bytes_differ": same_size and different > 0 and not unexpected,
    }


def compare(a, b, path_a, path_b, skip_raw):
    first = set(a["entries"])
    second = set(b["entries"])
    shared = sorted(first & second)
    content_keys = ("directory", "sha256", "size")
    metadata_keys = tuple(k for k in next(iter(a["entries"].values()), {}) if k not in content_keys)
    return {
        "archive_bytes_equal": a["archive_sha256"] == b["archive_sha256"],
        "archive_comment_equal": a["archive_comment"] == b["archive_comment"],
        "entry_order_equal": a["order"] == b["order"],
        "only_a": sorted(first - second),
        "only_b": sorted(second - first),
        "content_differences": [
            name for name in shared
            if any(a["entries"][name][key] != b["entries"][name][key] for key in content_keys)
        ],
        "metadata_differences": {
            key: [name for name in shared if a["entries"][name][key] != b["entries"][name][key]]
            for key in metadata_keys
        },
        "raw_bytes": (
            {"status": "NOT_RUN", "reason": "raw-byte comparison was not requested"}
            if skip_raw else raw_byte_comparison(
                path_a, path_b, set(a["timestamp_offsets"]) & set(b["timestamp_offsets"])
            )
        ),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive_a", type=Path)
    parser.add_argument("archive_b", type=Path)
    parser.add_argument("--output", type=Path, required=True, help="new JSON evidence file; never overwritten")
    parser.add_argument("--skip-raw", action="store_true", help="skip the byte-offset comparison")
    args = parser.parse_args()
    a = snapshot(args.archive_a)
    b = snapshot(args.archive_b)
    comparison = compare(a, b, args.archive_a, args.archive_b, args.skip_raw)
    result = {"schema": 1, "a": a, "b": b, "comparison": comparison}
    with args.output.open("x", encoding="utf-8", newline="\n") as out:
        json.dump(result, out, indent=2, ensure_ascii=True)
        out.write("\n")
    summary = {
        "a_sha256": a["archive_sha256"],
        "b_sha256": b["archive_sha256"],
        "a_entries": len(a["entries"]),
        "b_entries": len(b["entries"]),
        "archive_bytes_equal": comparison["archive_bytes_equal"],
        "entry_order_equal": comparison["entry_order_equal"],
        "only_a": comparison["only_a"],
        "only_b": comparison["only_b"],
        "content_differences": comparison["content_differences"],
        "metadata_difference_counts": {k: len(v) for k, v in comparison["metadata_differences"].items()},
        "raw_bytes": comparison["raw_bytes"],
    }
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
