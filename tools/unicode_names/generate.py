#!/usr/bin/env python3
"""Build DotPython's pinned Unicode-16 names resource without network access.

Generation uses only checked-in Unicode data. --verify-cpython additionally
qualifies both name lookup and namereplace against CPython 3.14.7 / UCD 16.
"""
from __future__ import annotations

import argparse
import bisect
import hashlib
import json
from pathlib import Path
import platform
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / "third_party/unicode/16.0.0"
OUTPUT = ROOT / "src/DotPython.Runtime.Managed/Unicode/UnicodeNames16.bin"
MAGIC = b"DPYUN16\0"
HEADER = struct.Struct("<8sII")
ENTRY = struct.Struct("<IIHH")
RANGE = struct.Struct("<IIIHH")
ALIAS_START = 0xF0000
SEQUENCE_START = 0xF0200
TANGUT_RANGES = {(0x17000, 0x187F7), (0x18D00, 0x18D08)}


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_sources() -> dict:
    provenance = json.loads((DATA / "PROVENANCE.json").read_text(encoding="utf-8"))
    if provenance["unicode_version"] != "16.0.0":
        raise ValueError("This generator requires pinned Unicode 16.0.0 data")
    for source in provenance["sources"]:
        content = (DATA / source["file"]).read_bytes()
        if sha256(content) != source["sha256"]:
            raise ValueError(f"Source checksum mismatch: {source['file']}")
    return provenance


def fields(filename: str):
    for number, line in enumerate((DATA / filename).read_text(encoding="utf-8").splitlines(), 1):
        line = line.split("#", 1)[0].strip()
        if line:
            yield number, [field.strip() for field in line.split(";")]


def parse_names():
    explicit = []
    ranges = []
    excluded = set()
    unicode_names = 0
    previous_end = -1
    for number, row in fields("DerivedName.txt"):
        if len(row) != 2:
            raise ValueError(f"DerivedName.txt:{number}: expected two fields")
        codepoints, name = row
        endpoints = codepoints.split("..")
        start = int(endpoints[0], 16)
        end = int(endpoints[-1], 16)
        if len(endpoints) > 2 or not previous_end < start <= end < 0x110000:
            raise ValueError(f"DerivedName.txt:{number}: overlapping/unordered/invalid range")
        previous_end = end
        name.encode("ascii")
        unicode_names += end - start + 1
        if "*" in name:
            if name.count("*") != 1 or not name.endswith("*"):
                raise ValueError(f"DerivedName.txt:{number}: unsupported name pattern")
            if (start, end) in TANGUT_RANGES:
                if name != "TANGUT IDEOGRAPH-*":
                    raise ValueError("Tangut compatibility exclusion no longer matches the source")
                excluded.add((start, end))
            else:
                ranges.append((start, end, name[:-1]))
        else:
            if start != end:
                raise ValueError(f"DerivedName.txt:{number}: non-pattern ranges are unsupported")
            explicit.append((start, name))
    if excluded != TANGUT_RANGES:
        raise ValueError("Expected exactly two Unicode-16 Tangut ranges")

    aliases = []
    for number, row in fields("NameAliases.txt"):
        if len(row) != 3:
            raise ValueError(f"NameAliases.txt:{number}: expected three fields")
        codepoint, name, _kind = row
        if not 0 <= int(codepoint, 16) < 0x110000:
            raise ValueError("Alias target outside Unicode range")
        aliases.append(name)
    if ALIAS_START + len(aliases) >= SEQUENCE_START:
        raise ValueError("CPython alias and sequence storage ranges overlap")

    sequences = []
    for number, row in fields("NamedSequences.txt"):
        if len(row) != 2:
            raise ValueError(f"NamedSequences.txt:{number}: expected two fields")
        name, codepoints = row
        sequence = [int(point, 16) for point in codepoints.split()]
        if not 2 <= len(sequence) <= 4 or any(point > 0xFFFF for point in sequence):
            raise ValueError("Named sequence no longer fits CPython's pinned storage assumptions")
        sequences.append(name)

    # CPython's C name API exposes these pseudo-entries to namereplace, while
    # unicodedata.name intentionally hides them. Keep file order, not name order.
    explicit.extend((ALIAS_START + index, name) for index, name in enumerate(aliases))
    explicit.extend((SEQUENCE_START + index, name) for index, name in enumerate(sequences))
    explicit.sort()
    intervals = sorted([(point, point) for point, _ in explicit] + [(start, end) for start, end, _ in ranges])
    if any(left[1] >= right[0] for left, right in zip(intervals, intervals[1:])):
        raise ValueError("Names and compatibility pseudo-entries overlap")
    metadata = {
        "unicode_version": "16.0.0",
        "unicode_named_codepoints": unicode_names,
        "excluded_tangut_codepoints": sum(end - start + 1 for start, end in excluded),
        "public_named_codepoints": unicode_names - sum(end - start + 1 for start, end in excluded),
        "alias_count": len(aliases),
        "sequence_count": len(sequences),
        "explicit_count": len(explicit),
        "range_count": len(ranges),
    }
    return explicit, ranges, metadata


def build(explicit, ranges):
    blob_start = HEADER.size + len(explicit) * ENTRY.size + len(ranges) * RANGE.size
    blob = bytearray()
    locations = {}

    def location(name):
        if name not in locations:
            encoded = name.encode("ascii")
            if not encoded or len(encoded) > 0xFFFF or any(byte < 0x20 or byte > 0x7E for byte in encoded):
                raise ValueError("Resource names must be nonempty printable ASCII with 16-bit lengths")
            locations[name] = (blob_start + len(blob), len(encoded))
            blob.extend(encoded)
        return locations[name]

    records = bytearray()
    for point, name in explicit:
        offset, length = location(name)
        records.extend(ENTRY.pack(point, offset, length, 0))
    for start, end, prefix in ranges:
        offset, length = location(prefix)
        records.extend(RANGE.pack(start, end, offset, length, 0))
    return HEADER.pack(MAGIC, len(explicit), len(ranges)) + records + blob


class Resource:
    """Independent binary reader used to qualify generated bytes, not source rows."""

    def __init__(self, data):
        self.data = data
        magic, explicit_count, range_count = HEADER.unpack_from(data)
        if magic != MAGIC:
            raise ValueError("Invalid binary magic")
        self.points = []
        self.entries = []
        cursor = HEADER.size
        for _ in range(explicit_count):
            point, offset, length, reserved = ENTRY.unpack_from(data, cursor)
            if reserved:
                raise ValueError("Nonzero explicit reserved field")
            self.points.append(point)
            self.entries.append(data[offset:offset + length].decode("ascii"))
            cursor += ENTRY.size
        self.starts = []
        self.ranges = []
        for _ in range(range_count):
            start, end, offset, length, reserved = RANGE.unpack_from(data, cursor)
            if reserved:
                raise ValueError("Nonzero range reserved field")
            self.starts.append(start)
            self.ranges.append((end, data[offset:offset + length].decode("ascii")))
            cursor += RANGE.size

    def get(self, point, include_aliases_and_sequences=False):
        # There are no Unicode Name-property values in PUA15; CPython places its
        # two internal lookup tables there and public unicodedata.name hides them.
        if not include_aliases_and_sequences and 0xF0000 <= point <= 0xFFFFD:
            return None
        index = bisect.bisect_left(self.points, point)
        if index < len(self.points) and self.points[index] == point:
            return self.entries[index]
        index = bisect.bisect_right(self.starts, point) - 1
        if index >= 0 and point <= self.ranges[index][0]:
            return self.ranges[index][1] + f"{point:04X}"
        return None


def qualify(data):
    import unicodedata
    if platform.python_implementation() != "CPython" or sys.version_info[:3] != (3, 14, 7):
        raise ValueError("--verify-cpython requires CPython 3.14.7")
    if unicodedata.unidata_version != "16.0.0":
        raise ValueError("--verify-cpython requires Unicode database 16.0.0")
    resource = Resource(data)
    public_digest = hashlib.sha256()
    capi_digest = hashlib.sha256()
    public_count = 0
    capi_count = 0
    for point in range(0x110000):
        character = chr(point)
        name = resource.get(point)
        oracle_name = unicodedata.name(character, None)
        if name != oracle_name:
            raise ValueError(f"U+{point:04X}: name mismatch {name!r} != {oracle_name!r}")
        capi_name = resource.get(point, True)
        expected = (
            bytes([point]) if point < 128
            else f"\\N{{{capi_name}}}".encode("ascii") if capi_name is not None
            else f"\\x{point:02x}".encode("ascii") if point <= 0xFF
            else f"\\u{point:04x}".encode("ascii") if point <= 0xFFFF
            else f"\\U{point:08x}".encode("ascii")
        )
        actual = character.encode("ascii", "namereplace")
        oracle_capi_name = (
            oracle_name if point < 128
            else actual[3:-1].decode("ascii") if actual.startswith(b"\\N{") and actual.endswith(b"}")
            else None
        )
        if oracle_name is not None:
            public_digest.update(struct.pack("<I", point) + oracle_name.encode("ascii") + b"\0")
            public_count += 1
        if oracle_capi_name is not None:
            capi_digest.update(struct.pack("<I", point) + oracle_capi_name.encode("ascii") + b"\0")
            capi_count += 1
        if expected != actual:
            raise ValueError(f"U+{point:04X}: namereplace mismatch {expected!r} != {actual!r}")
    return {
        "python": platform.python_version(),
        "unicode_database": unicodedata.unidata_version,
        "codepoints_checked_per_surface": 0x110000,
        "surfaces": ["unicodedata.name", "ascii namereplace"],
        "comparisons": 2 * 0x110000,
        "mismatches": 0,
        "public_names": {"count": public_count, "sha256": public_digest.hexdigest()},
        "capi_names": {"count": capi_count, "sha256": capi_digest.hexdigest()},
        "digest_record": "codepoint uint32 little-endian, ASCII name, NUL; named codepoints in ascending order",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if the checked-in binary differs; do not write it")
    parser.add_argument("--verify-cpython", action="store_true", help="exhaustively compare generated binary lookups with pinned CPython")
    parser.add_argument("--report", type=Path, help="optional JSON report path")
    args = parser.parse_args()
    provenance = read_sources()
    explicit, ranges, report = parse_names()
    data = build(explicit, ranges)
    report.update(bytes=len(data), sha256=sha256(data), magic_hex=MAGIC.hex())
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_bytes() != data:
            raise ValueError("UnicodeNames16.bin differs; regenerate with tools/unicode_names/generate.py")
        print("UnicodeNames16.bin is reproducible")
    else:
        OUTPUT.parent.mkdir(parents=True, exist_ok=True)
        OUTPUT.write_bytes(data)
        print(f"Generated {OUTPUT.relative_to(ROOT)}")
    expected_artifact = provenance.get("artifact")
    if expected_artifact and (report["bytes"] != expected_artifact["bytes"] or report["sha256"] != expected_artifact["sha256"]):
        raise ValueError("Generated artifact differs from pinned provenance")
    if args.verify_cpython:
        report["qualification"] = qualify(data)
    if args.report:
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError) as error:
        raise SystemExit(str(error)) from error
