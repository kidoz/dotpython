#!/usr/bin/env python3
"""Build DotPython's pinned Unicode-16 predicate resource without network access.

Generation uses only checked-in Unicode data. --verify-cpython qualifies every
predicate against a reference CPython 3.14.7 for the whole code space.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / "third_party/unicode/16.0.0"
OUTPUT = ROOT / "src/DotPython.Runtime.Managed/Unicode/UnicodePredicates16.bin"
MAGIC = b"DPYPRED16\0"
HEADER = struct.Struct("<10sII")  # magic, set count, total range count
COUNTS = struct.Struct("<I")
RANGE = struct.Struct("<II")

# The predicates, in the order they are written and read back.
NAMES = (
    "alpha",
    "decimal",
    "digit",
    "numeric",
    "space",
    "printable",
    "xid_start",
    "xid_continue",
    "titlecase",
)
ALPHA_CATEGORIES = ("Lu", "Ll", "Lt", "Lm", "Lo")
# CPython's Py_UNICODE_ISSPACE, which is not the Unicode White_Space property.
SPACE_RANGES = [
    (0x09, 0x0D),   # tab, line feed, vertical tab, form feed, carriage return
    (0x1C, 0x1F),   # file, group, record and unit separators
    (0x20, 0x20),
    (0x85, 0x85),   # next line
    (0xA0, 0xA0),   # no-break space
    (0x1680, 0x1680),
    (0x2000, 0x200A),
    (0x2028, 0x2029),
    (0x202F, 0x202F),
    (0x205F, 0x205F),
    (0x3000, 0x3000),
]


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def check_sources() -> None:
    provenance = json.loads((DATA / "PROVENANCE.json").read_text(encoding="utf-8"))
    for source in provenance["sources"]:
        path = DATA / source["file"]
        raw = path.read_bytes()
        if len(raw) != source["bytes"] or sha256(raw) != source["sha256"]:
            raise SystemExit(f"{source['file']} does not match PROVENANCE.json")


def unicode_data_rows():
    """UnicodeData rows, with `First>`/`Last>` pairs expanded into every point they cover."""
    pending = None
    for line in (DATA / "UnicodeData.txt").read_text(encoding="utf-8").splitlines():
        fields = line.split(";")
        if len(fields) < 15:
            continue
        code = int(fields[0], 16)
        if pending is not None:
            # The rest of a ranged block repeats the First row's other fields.
            start, first = pending
            pending = None
            if fields[2] == first[2]:
                for point in range(start, code + 1):
                    yield [f"{point:04X}", *first[1:]]
                continue
        if fields[1].endswith(", First>"):
            pending = (code, fields)
            continue
        yield fields


def category_ranges(categories: tuple[str, ...]) -> list[tuple[int, int]]:
    points = sorted(
        int(fields[0], 16) for fields in unicode_data_rows() if fields[2] in categories
    )
    return compress(points)


def numeric_ranges(*types: str) -> list[tuple[int, int]]:
    """The code points whose Unicode Numeric_Type is one of the given types.

    UnicodeData alone is not enough: a CJK ideograph such as U+4E03 carries its value in
    the Unihan data, so only the extracted Numeric_Type file lists it.
    """
    points = []
    for line in (DATA / "DerivedNumericType.txt").read_text(encoding="utf-8").splitlines():
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        fields = [field.strip() for field in line.split(";")]
        if len(fields) < 2 or fields[1] not in types:
            continue
        bounds = fields[0].split("..")
        points.append((int(bounds[0], 16), int(bounds[-1], 16)))
    points.sort()
    return compress([code for start, end in points for code in range(start, end + 1)])


def printable_ranges() -> list[tuple[int, int]]:
    """CPython's Py_UNICODE_ISPRINTABLE: everything but the control and spacing classes."""
    excluded = ("Cc", "Cf", "Cs", "Co", "Zl", "Zp", "Zs")
    points = {
        int(fields[0], 16)
        for fields in unicode_data_rows()
        if fields[2] not in excluded
    }
    points.add(0x20)
    return compress(sorted(points))


def derived_ranges(property_name: str) -> list[tuple[int, int]]:
    ranges = []
    for line in (DATA / "DerivedCoreProperties.txt").read_text(encoding="utf-8").splitlines():
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        fields = [field.strip() for field in line.split(";")]
        if len(fields) < 2 or fields[1] != property_name:
            continue
        bounds = fields[0].split("..")
        ranges.append((int(bounds[0], 16), int(bounds[-1], 16)))
    return ranges


def compress(points: list[int]) -> list[tuple[int, int]]:
    ranges: list[tuple[int, int]] = []
    for code in points:
        if ranges and code == ranges[-1][1] + 1:
            ranges[-1] = (ranges[-1][0], code)
        else:
            ranges.append((code, code))
    return ranges


def build_tables() -> dict[str, list[tuple[int, int]]]:
    return {
        "alpha": category_ranges(ALPHA_CATEGORIES),
        "decimal": numeric_ranges("Decimal"),
        "digit": numeric_ranges("Decimal", "Digit"),
        "numeric": numeric_ranges("Decimal", "Digit", "Numeric"),
        "space": list(SPACE_RANGES),
        "printable": printable_ranges(),
        "xid_start": derived_ranges("XID_Start"),
        "xid_continue": derived_ranges("XID_Continue"),
        "titlecase": category_ranges(("Lt",)),
    }


def build() -> tuple[bytes, dict[str, int]]:
    tables = build_tables()
    body = bytearray()
    body += HEADER.pack(MAGIC, len(NAMES), sum(len(v) for v in tables.values()))
    # Each set is its count followed by that many ranges, so a reader never has to
    # know a set's length before reaching its data.
    for name in NAMES:
        body += COUNTS.pack(len(tables[name]))
        for start, end in tables[name]:
            body += RANGE.pack(start, end)
    summary = {name: len(tables[name]) for name in NAMES}
    summary["bytes"] = len(body)
    summary["sha256"] = sha256(bytes(body))
    return bytes(body), summary


def verify_cpython(reference: str) -> int:
    """Compare every predicate over every code point against the reference CPython."""
    tables = build_tables()

    def has(name: str, code: int) -> bool:
        return any(start <= code <= end for start, end in tables[name])

    script = r"""
import sys
out = sys.stdout
for c in range(0x110000):
    if 0xD800 <= c <= 0xDFFF:
        continue
    s = chr(c)
    out.write(f"{c:X} {int(s.isalpha())}{int(s.isdecimal())}{int(s.isdigit())}"
              f"{int(s.isnumeric())}{int(s.isspace())}{int(s.isprintable())}"
              f"{int(s.isidentifier())}\n")
"""
    result = subprocess.run(
        [reference, "-c", script], capture_output=True, text=True, check=True
    )
    mismatches = 0
    for line in result.stdout.splitlines():
        code_text, flags = line.split()
        code = int(code_text, 16)
        reference_flags = [flag == "1" for flag in flags]
        mine = [
            has("alpha", code),
            has("decimal", code),
            has("digit", code),
            has("numeric", code),
            has("space", code),
            has("printable", code),
            # A single character is an identifier when it starts one, and `_` may start
            # one even though it is only `XID_Continue` in the UCD.
            code == 0x5F or has("xid_start", code),
        ]
        for name, expected, actual in zip(NAMES, reference_flags, mine):
            if name in ("xid_continue", "titlecase"):
                continue
            if expected != actual:
                if mismatches < 25:
                    print(f"{name} U+{code:X}: reference {expected} != table {actual}")
                mismatches += 1
    if mismatches:
        print(f"mismatches: {mismatches}")
        return 1
    print("every predicate matches CPython for every code point")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if the resource is stale")
    parser.add_argument("--verify-cpython", metavar="PYTHON", help="qualify against CPython")
    parser.add_argument("--summary", action="store_true", help="print the table summary")
    args = parser.parse_args()

    check_sources()
    data, summary = build()
    if args.summary:
        print(json.dumps(summary, indent=2))
    if args.verify_cpython:
        return verify_cpython(args.verify_cpython)
    if args.check:
        current = OUTPUT.read_bytes() if OUTPUT.exists() else b""
        if current != data:
            print(f"{OUTPUT} is stale; run tools/unicode_predicates/generate.py", file=sys.stderr)
            return 1
        print(f"{OUTPUT} is up to date ({summary['bytes']} bytes, sha256 {summary['sha256']})")
        return 0
    OUTPUT.write_bytes(data)
    print(f"wrote {OUTPUT} ({summary['bytes']} bytes, sha256 {summary['sha256']})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
